using PalCalc.Model;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PalCalc.Solver
{
    /// <summary>
    /// Determines which Pals can be bred from a set of owned Pals using transitive closure.
    /// </summary>
    public static class BreedingReachability
    {
        /// <summary>
        /// Calculates all Pals that can theoretically be reached through breeding from the owned Pals.
        /// Uses a transitive closure algorithm with parallel processing: iteratively breed all reachable Pals together until no new children are discovered.
        /// </summary>
        /// <param name="db">The breeding database</param>
        /// <param name="ownedPals">The set of Pals currently owned by the player</param>
        /// <returns>A HashSet containing all reachable PalIds (including the original owned ones)</returns>
        public static HashSet<PalId> GetReachablePals(PalBreedingDB db, IEnumerable<Pal> ownedPals)
        {
            var startTime = DateTime.Now;
            var reachable = new HashSet<PalId>();
            var ownedPalSet = ownedPals.ToList();

            // Add all initially owned Pals to reachable set
            foreach (var pal in ownedPalSet)
            {
                reachable.Add(pal.Id);
            }

            // Check if we have at least one male and one female
            if (!CanBreed(ownedPalSet))
            {
                return reachable;
            }

            int iterationCount = 0;
            bool newChildrenFound = true;
            while (newChildrenFound)
            {
                iterationCount++;
                var iterationStartTime = DateTime.Now;
                newChildrenFound = false;

                // Get all currently reachable Pals as actual Pal objects
                var reachablePals = ownedPalSet
                    .Where(p => reachable.Contains(p.Id))
                    .ToList();

                // Use a concurrent bag to collect new children from parallel processing
                var newChildren = new ConcurrentBag<Pal>();

                // Create list of all pairs to process
                var pairs = new List<(Pal, Pal)>();
                for (int i = 0; i < reachablePals.Count; i++)
                {
                    for (int j = i; j < reachablePals.Count; j++)
                    {
                        pairs.Add((reachablePals[i], reachablePals[j]));
                    }
                }

                // Process all pairs in parallel
                Parallel.ForEach(pairs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, pair =>
                {
                    var children = GetBreedingChildren(db, pair.Item1, pair.Item2);
                    foreach (var child in children)
                    {
                        newChildren.Add(child);
                    }
                });

                // Add all new children to the reachable set
                lock (reachable)
                {
                    foreach (var child in newChildren.Distinct())
                    {
                        if (reachable.Add(child.Id))
                        {
                            newChildrenFound = true;
                            ownedPalSet.Add(child);
                        }
                    }
                }

                var iterationDuration = DateTime.Now - iterationStartTime;
                var totalDuration = DateTime.Now - startTime;
                Serilog.Log.Information("Iteration {iteration}: processed {pairCount} pairs, found {newCount} new children, took {iterationMs}ms (total {totalMs}ms)",
                    iterationCount, pairs.Count, newChildren.Distinct().Count(), iterationDuration.TotalMilliseconds, totalDuration.TotalMilliseconds);
            }

            var totalTime = DateTime.Now - startTime;
            Serilog.Log.Information("Reachability calculation completed in {iterations} iterations, {reachableCount} total reachable pals, took {totalMs}ms total",
                iterationCount, reachable.Count, totalTime.TotalMilliseconds);

            return reachable;
        }

        /// <summary>
        /// Checks if breeding is possible with the given set of Pals.
        /// Requires at least one male and one female.
        /// </summary>
        private static bool CanBreed(IEnumerable<Pal> ownedPals)
        {
            // For simplicity, we check if there are enough different Pals or if we can reasonably assume gender diversity
            // Since we don't have gender info in Pal class itself, we consider breeding possible if we have multiple Pals
            // or the same Pal multiple times (different genders are possible)
            var pals = ownedPals.ToList();
            
            // If we have only one Pal, we need at least 2 instances for potential gender diversity
            if (pals.Count == 1)
            {
                return false; // Conservative: need explicit multiple instances
            }

            // If we have multiple different Pals or multiple instances, assume gender diversity is possible
            return pals.Count >= 2 || pals.Distinct().Count() >= 2;
        }

        /// <summary>
        /// Gets all possible children from breeding two Pals (considering all gender combinations).
        /// </summary>
        private static HashSet<Pal> GetBreedingChildren(PalBreedingDB db, Pal parent1, Pal parent2)
        {
            var children = new HashSet<Pal>();

            // Check if BreedingByParent has entries for either Pal
            if (!db.BreedingByParent.ContainsKey(parent1) && !db.BreedingByParent.ContainsKey(parent2))
            {
                return children;
            }

            // Try parent1 as first parent with parent2 as second parent
            if (db.BreedingByParent.TryGetValue(parent1, out var breedingMap1))
            {
                if (breedingMap1.TryGetValue(parent2, out var breedingResults1))
                {
                    foreach (var result in breedingResults1)
                    {
                        children.Add(result.Child);
                    }
                }
            }

            // Try parent2 as first parent with parent1 as second parent
            if (db.BreedingByParent.TryGetValue(parent2, out var breedingMap2))
            {
                if (breedingMap2.TryGetValue(parent1, out var breedingResults2))
                {
                    foreach (var result in breedingResults2)
                    {
                        children.Add(result.Child);
                    }
                }
            }

            return children;
        }
    }
}
