using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GongSolutions.Wpf.DragDrop;
using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.Solver;
using PalCalc.UI.Model;
using PalCalc.UI.ViewModel.Mapped;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace PalCalc.UI.ViewModel
{

    public partial class PalTargetListViewModel : ObservableObject, IDropTarget
    {
        private static ILogger logger = Log.ForContext<PalTargetListViewModel>();
        private readonly System.Windows.Threading.Dispatcher dispatcher;

        public PalTargetListViewModel()
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            
            targets = new ObservableCollection<PalSpecifierViewModel>
            {
                PalSpecifierViewModel.New
            };

            Targets = new ReadOnlyObservableCollection<PalSpecifierViewModel>(targets);
            SelectedTarget = PalSpecifierViewModel.New;
            
            ReloadSaveCommand = new RelayCommand(ReloadSave, CanReloadSave);
            CalculateReachablePalsCommand = new RelayCommand(CalculateReachablePals, CanCalculateReachablePals);
            // ReachablePalsVm will be lazily initialized when needed
        }

        public PalTargetListViewModel(IEnumerable<PalSpecifierViewModel> existingSpecs)
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            
            targets = new ObservableCollection<PalSpecifierViewModel>()
            {
                PalSpecifierViewModel.New
            };

            foreach (var spec in existingSpecs)
                targets.Add(spec);

            Targets = new ReadOnlyObservableCollection<PalSpecifierViewModel>(targets);
            SelectedTarget = PalSpecifierViewModel.New;
            
            ReloadSaveCommand = new RelayCommand(ReloadSave, CanReloadSave);
            CalculateReachablePalsCommand = new RelayCommand(CalculateReachablePals, CanCalculateReachablePals);
            // ReachablePalsVm will be lazily initialization when needed
        }

        private ObservableCollection<PalSpecifierViewModel> targets;
        public ReadOnlyObservableCollection<PalSpecifierViewModel> Targets { get; }

        [ObservableProperty]
        private PalSpecifierViewModel selectedTarget;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSaveGameLoaded))]
        private bool isCalculatingReachability = false;

        [ObservableProperty]
        private ReachablePalsViewModel reachablePalsVm;

        private HashSet<PalId> reachablePals;
        private HashSet<PalId> ownedPals;
        private CachedSaveGame currentSaveGame;
        private ISavesLocation currentContainerLocation;
        
        // Reference to the current PalTarget for accessing Source Pals selections
        private PalTargetViewModel currentPalTarget;
        
        // Lazy initialization to ensure proper dispatcher binding
        private ReachablePalsViewModel lazyReachablePalsVm;
        private ReachablePalsViewModel GetOrCreateReachablePalsVm()
        {
            if (lazyReachablePalsVm == null)
            {
                lazyReachablePalsVm = new ReachablePalsViewModel();
                ReachablePalsVm = lazyReachablePalsVm;
            }
            return lazyReachablePalsVm;
        }
        
        public void SetCurrentPalTarget(PalTargetViewModel palTarget)
        {
            currentPalTarget = palTarget;
            
            // Subscribe to PalSelected event from ReachablePalsViewModel
            var reachablePalsVm = GetOrCreateReachablePalsVm();
            if (reachablePalsVm != null)
            {
                reachablePalsVm.PalSelected -= OnReachablePalSelected;
                reachablePalsVm.PalSelected += OnReachablePalSelected;
            }
        }
        
        private void OnReachablePalSelected(PalViewModel selectedPal)
        {
            if (selectedPal == null || currentPalTarget == null) return;
            
            logger.Information("Reachable pal selected: {palName}", selectedPal.ModelObject.Name);
            
            // Get the current source selections from PalSource, not from CurrentPalSpecifier
            // because CurrentPalSpecifier might not have them set yet
            var currentSelections = currentPalTarget.PalSource?.Selections;
            logger.Information("OnReachablePalSelected: Current PalSource has {count} selections", currentSelections?.Count ?? 0);
            if (currentSelections != null)
            {
                foreach (var sel in currentSelections)
                {
                    logger.Information("  - Current selection: {id}", sel.SerializedId);
                }
            }
            
            // Create a new target with the selected pal
            var newTarget = new PalSpecifierViewModel(Guid.NewGuid().ToString(), new PalCalc.Solver.PalSpecifier
            {
                Pal = selectedPal.ModelObject,
                RequiredPassives = new List<PassiveSkill>(),
                OptionalPassives = new List<PassiveSkill>(),
                RequiredGender = PalGender.WILDCARD,
                IV_HP = 0,
                IV_Attack = 0,
                IV_Defense = 0,
            });
            
            // Copy the source selections from the current PalSource
            if (currentSelections != null)
            {
                newTarget.PalSourceSelections = currentSelections.ToList();
            }
            else
            {
                newTarget.PalSourceSelections = new List<PalCalc.UI.ViewModel.Solver.IPalSourceTreeSelection>();
            }
            
            logger.Information("OnReachablePalSelected: New target created with {count} source selections", newTarget.PalSourceSelections?.Count ?? 0);
            if (newTarget.PalSourceSelections != null)
            {
                foreach (var sel in newTarget.PalSourceSelections)
                {
                    logger.Information("  - New target selection: {id}", sel.SerializedId);
                }
            }
            
            // Add to targets list
            Add(newTarget);
            SelectedTarget = newTarget;
            
            // Request to run solver
            OnRunSolverRequested?.Invoke(newTarget);
            
            logger.Information("New target created for: {palName}, solver run requested", selectedPal.ModelObject.Name);
        }
        
        public event Action<PalSpecifierViewModel> OnRunSolverRequested;

        public event Action<PalTargetListViewModel> OrderChanged;

        public IRelayCommand ReloadSaveCommand { get; private set; }
        public IRelayCommand CalculateReachablePalsCommand { get; private set; }

        private bool CanReloadSave() => HasSaveGameLoaded;

        private void ReloadSave()
        {
            if (currentSaveGame == null) return;
            
            // Get current game settings - IMPORTANT for proper reload!
            var gameSettings = GameSettingsViewModel.Load(currentSaveGame.UnderlyingSave).ModelObject;
            Storage.ReloadSave(currentContainerLocation, currentSaveGame.UnderlyingSave, PalDB.LoadEmbedded(), gameSettings);
            
            logger.Information("Manual reload of SaveGame initiated");
        }

        private bool CanCalculateReachablePals() => HasSaveGameLoaded && !IsCalculatingReachability;

        private void CalculateReachablePals()
        {
            if (currentSaveGame == null)
            {
                logger.Debug("CalculateReachablePals: SaveGame is null");
                return;
            }

            IsCalculatingReachability = true;
            CalculateReachablePalsCommand.NotifyCanExecuteChanged();

            Task.Run(() =>
            {
                // Use normal thread priority for better CPU utilization
                System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
                
                try
                {
                    var db = PalDB.LoadEmbedded();
                    var breedingDb = PalBreedingDB.LoadEmbedded(db);
                    
                    // Get the filtered owned pals based on PalSource selections from the current PalTarget
                    var ownedPalInstances = new List<PalInstance>();
                    var sourceSelections = currentPalTarget?.PalSource?.Selections;
                    
                    logger.Information("CalculateReachablePals: SelectedTarget={target}, Selections={selectionCount}", 
                        SelectedTarget?.Label?.Value ?? "null", sourceSelections?.Count ?? -1);
                    
                    if (sourceSelections?.Any() == true)
                    {
                        logger.Information("Calculating reachable pals with {selectionCount} source selections", sourceSelections.Count);
                        
                        // Log which selections are being used
                        foreach (var sel in sourceSelections)
                        {
                            logger.Information("  Selection: {selectionType}", sel.SerializedId);
                        }
                        
                        // Filter OwnedPals based on the selections
                        foreach (var palInstance in currentSaveGame.OwnedPals)
                        {
                            if (sourceSelections.Any(s => s.Matches(currentSaveGame, palInstance)))
                            {
                                ownedPalInstances.Add(palInstance);
                            }
                        }
                    }
                    else
                    {
                        // If no selections, use all owned pals
                        logger.Information("No PalSource selections found, using all owned pals");
                        ownedPalInstances = currentSaveGame.OwnedPals.ToList();
                    }
                    
                    var ownedPalList = ownedPalInstances.Select(p => p.Pal).Distinct();
                    logger.Information("Starting reachable pals calculation for {count} filtered owned pals (from {total} total)", 
                        ownedPalInstances.Count, currentSaveGame.OwnedPals.Count);
                    
                    // Calculate on background thread (this is the slow part)
                    var calculatedReachablePals = BreedingReachability.GetReachablePals(breedingDb, ownedPalList);
                    
                    // Store the result
                    reachablePals = calculatedReachablePals;
                    
                    // Update UI on the main application dispatcher
                    var mainDispatcher = System.Windows.Application.Current.Dispatcher;
                    mainDispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            logger.Information("UI thread callback: About to get reachable pals VM");
                            
                            // Get or create ReachablePalsVm
                            var reachablePalsVm = GetOrCreateReachablePalsVm();
                            
                            if (reachablePalsVm == null)
                            {
                                logger.Error("UI thread callback: reachablePalsVm is null!");
                                return;
                            }
                            
                            logger.Information("UI thread callback: Updating {reachableCount} reachable pals", calculatedReachablePals.Count);
                            
                            // Call UpdateReachablePals on the UI thread
                            reachablePalsVm.UpdateReachablePals(calculatedReachablePals, ownedPals, db, currentSaveGame.Players);
                            
                            logger.Information("UI thread callback: Update completed successfully");
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "Error updating reachable pals UI");
                        }
                        finally
                        {
                            IsCalculatingReachability = false;
                            CalculateReachablePalsCommand.NotifyCanExecuteChanged();
                            logger.Information("UI thread callback: Finished");
                        }
                    });
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Error calculating reachable pals");
                    reachablePals = null;
                    
                    // Update state on main dispatcher
                    var mainDispatcher = System.Windows.Application.Current.Dispatcher;
                    mainDispatcher.BeginInvoke(() =>
                    {
                        IsCalculatingReachability = false;
                        CalculateReachablePalsCommand.NotifyCanExecuteChanged();
                    });
                }
            });
        }

        public bool HasSaveGameLoaded => currentSaveGame != null;

        public void Add(PalSpecifierViewModel value) => targets.Insert(1, value);
        public void Remove(PalSpecifierViewModel value) => targets.Remove(value);

        public void Replace(PalSpecifierViewModel oldValue, PalSpecifierViewModel newValue)
        {
            var oldIndex = targets.IndexOf(oldValue);
            targets[oldIndex] = newValue;
        }

        public void UpdateCachedData(CachedSaveGame csg, GameSettings settings, ISavesLocation containerLocation = null)
        {
            foreach (var target in targets)
                target.CurrentResults?.UpdateCachedData(csg, settings);
            
            currentSaveGame = csg;
            currentContainerLocation = containerLocation;
            
            if (csg != null)
            {
                ownedPals = new HashSet<PalId>(csg.OwnedPals.Select(p => p.Pal.Id).Distinct());
            }
            else
            {
                ownedPals = null;
                reachablePals = null;
                
                // Clear reachable pals view when save is unloaded
                var reachablePalsVm = GetOrCreateReachablePalsVm();
                reachablePalsVm?.UpdateReachablePals(null, null, null, null);
            }
            
            OnPropertyChanged(nameof(HasSaveGameLoaded));
            ReloadSaveCommand?.NotifyCanExecuteChanged();
            CalculateReachablePalsCommand?.NotifyCanExecuteChanged();
            
            logger.Information("UpdateCachedData called: SaveGame={loaded}, OwnedPals={count}", 
                HasSaveGameLoaded, csg?.OwnedPals.Count ?? 0);
        }

        public bool IsBreedable(Pal pal)
        {
            return reachablePals?.Contains(pal.Id) ?? false;
        }

        public void DragOver(IDropInfo dropInfo)
        {
            if (!dropInfo.IsSameDragDropContextAsSource)
                return;

            var sourceItem = dropInfo.Data as PalSpecifierViewModel;
            var targetItem = dropInfo.TargetItem as PalSpecifierViewModel;

            if (
                sourceItem != null &&
                targetItem != null &&
                sourceItem != targetItem &&
                !targetItem.IsReadOnly &&
                Targets.Contains(sourceItem) &&
                dropInfo.InsertIndex > 0
            )
            {
                dropInfo.DropTargetAdorner = DropTargetAdorners.Insert;
                dropInfo.Effects = DragDropEffects.Move;
            }
        }

        public void Drop(IDropInfo dropInfo)
        {
            var sourceItem = dropInfo.Data as PalSpecifierViewModel;
            var targetItem = dropInfo.TargetItem as PalSpecifierViewModel;

            var sourceIndex = targets.IndexOf(sourceItem);
            var targetIndex = targets.IndexOf(targetItem);

            int newIndex = dropInfo.InsertIndex;
            if (sourceIndex < targetIndex) newIndex -= 1;

            if (sourceIndex == newIndex) return;

            targets.Move(targets.IndexOf(sourceItem), Math.Clamp(newIndex, 1, targets.Count - 1));
            OrderChanged?.Invoke(this);
        }
    }
}
