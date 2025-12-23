using CommunityToolkit.Mvvm.ComponentModel;
using PalCalc.Model;
using PalCalc.Solver;
using PalCalc.UI.ViewModel.Mapped;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;

namespace PalCalc.UI.ViewModel
{
    public partial class ReachablePalsViewModel : ObservableObject
    {
        private static ILogger logger = Log.ForContext<ReachablePalsViewModel>();
        private readonly Dispatcher creationDispatcher;

        public ReachablePalsViewModel()
        {
            creationDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            ReachablePals = new ObservableCollection<PalViewModel>();
            ReachablePalsView = CollectionViewSource.GetDefaultView(ReachablePals);
            ReachablePalsView.SortDescriptions.Add(new SortDescription(nameof(PalViewModel.ModelObject) + "." + nameof(Pal.Name), ListSortDirection.Ascending));
            
            PalDoubleClickedCommand = new RelayCommand<PalViewModel>(OnPalDoubleClicked);
        }

        [ObservableProperty]
        private ObservableCollection<PalViewModel> reachablePals;

        [ObservableProperty]
        private ICollectionView reachablePalsView;

        [ObservableProperty]
        private bool sortByRarity = false;

        [ObservableProperty]
        private int reachableCount = 0;

        [ObservableProperty]
        private int ownedCount = 0;
        
        [ObservableProperty]
        private bool showOnlyNew = false;
        
        private HashSet<PalId> ownedPalIds = new();
        
        public ICommand PalDoubleClickedCommand { get; }
        
        public event Action<PalViewModel> PalSelected;

        partial void OnSortByRarityChanged(bool value)
        {
            if (ReachablePalsView == null) return;

            ReachablePalsView.SortDescriptions.Clear();

            if (value)
            {
                ReachablePalsView.SortDescriptions.Add(new SortDescription(
                    nameof(PalViewModel.ModelObject) + "." + nameof(Pal.BreedingPower), 
                    ListSortDirection.Ascending));
            }
            else
            {
                ReachablePalsView.SortDescriptions.Add(new SortDescription(
                    nameof(PalViewModel.ModelObject) + "." + nameof(Pal.Name), 
                    ListSortDirection.Ascending));
            }

            ReachablePalsView.Refresh();
            logger.Debug("ReachablePals sorted by {sortBy}", value ? "Rarity" : "Name");
        }
        
        partial void OnShowOnlyNewChanged(bool value)
        {
            if (ReachablePalsView == null) return;
            
            if (value)
            {
                ReachablePalsView.Filter = pal => !ownedPalIds.Contains(((PalViewModel)pal).ModelObject.Id);
            }
            else
            {
                ReachablePalsView.Filter = null;
            }
            
            ReachablePalsView.Refresh();
            logger.Debug("ReachablePals filter changed: showOnlyNew={showOnlyNew}", value);
        }

        private void OnPalDoubleClicked(PalViewModel pal)
        {
            if (pal == null) return;
            
            logger.Information("Pal double-clicked: {palName}", pal.ModelObject.Name);
            PalSelected?.Invoke(pal);
        }

        public void UpdateReachablePals(HashSet<PalId> reachableIds, HashSet<PalId> ownedIds, PalDB db, List<PlayerInstance> players = null)
        {
            if (creationDispatcher.Thread != System.Threading.Thread.CurrentThread)
            {
                creationDispatcher.Invoke(() => UpdateReachablePals(reachableIds, ownedIds, db, players));
                return;
            }

            try
            {
                if (reachableIds == null || db == null)
                {
                    ReachablePals.Clear();
                    ReachableCount = 0;
                    OwnedCount = 0;
                    ownedPalIds.Clear();
                    logger.Information("UpdateReachablePals: reachableIds or db was null, cleared list");
                    return;
                }

                logger.Information("UpdateReachablePals called with {reachableCount} reachable IDs", reachableIds.Count);
                
                // Store owned pal IDs for filtering
                ownedPalIds = ownedIds ?? new HashSet<PalId>();

                var reachablePalObjects = reachableIds
                    .Select(id => db.PalsById.GetValueOrDefault(id))
                    .Where(p => p != null)
                    .OrderBy(p => p.Name)
                    .ToList();

                logger.Information("Resolved {resolvedCount} of {totalCount} reachable IDs to Pal objects", 
                    reachablePalObjects.Count, reachableIds.Count);

                ReachablePals.Clear();
                
                int addedCount = 0;
                foreach (var pal in reachablePalObjects)
                {
                    try
                    {
                        var palVm = PalViewModel.Make(pal);
                        ReachablePals.Add(palVm);
                        addedCount++;
                        
                        if (addedCount % 50 == 0)
                        {
                            logger.Debug("UpdateReachablePals: Added {count} pals so far", addedCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "UpdateReachablePals: Error creating PalViewModel for {palName}", pal.Name);
                    }
                }

                ReachableCount = reachableIds.Count;
                OwnedCount = ownedIds?.Count ?? 0;

                logger.Information("UpdateReachablePals: Finished - {reachableCount} reachable, {ownedCount} owned, {displayCount} displayed",
                    ReachableCount, OwnedCount, ReachablePals.Count);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error in UpdateReachablePals");
                throw;
            }
        }

        public Dispatcher GetOwnerDispatcher() => creationDispatcher;
    }
}
