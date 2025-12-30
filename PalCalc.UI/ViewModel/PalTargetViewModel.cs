using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalCalc.Model;
using PalCalc.UI.Model;
using PalCalc.UI.View;
using PalCalc.UI.ViewModel.Mapped;
using PalCalc.UI.ViewModel.Presets;
using PalCalc.UI.ViewModel.Solver;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace PalCalc.UI.ViewModel
{
    /// <summary>
    /// View-model for the pal target settings on the right side of the main window. Manages the pal specifier
    /// pal sources, presets, etc.
    /// 
    /// This is more of a mediator than a proper view-wmodel object and isn't involved in serialization.
    /// </summary>
    public partial class PalTargetViewModel : ObservableObject
    {
        private static ILogger logger = Log.ForContext<PalTargetViewModel>();
        private SaveGameViewModel sourceSave;
        private bool suppressPalSourcePropertyChanged = false;

        public PalTargetViewModel() : this(null, PalSpecifierViewModel.New, PassiveSkillsPresetCollectionViewModel.DesignerInstance) { }

        public PalTargetViewModel(SaveGameViewModel sourceSave, PalSpecifierViewModel initial, PassiveSkillsPresetCollectionViewModel presets)
        {
            this.sourceSave = sourceSave;

            if (initial.IsReadOnly)
            {
                InitialPalSpecifier = null;
                CurrentPalSpecifier = new PalSpecifierViewModel(Guid.NewGuid().ToString(), null);

                PalSource = new PalSourceTreeViewModel(sourceSave.CachedValue);
            }
            else
            {
                InitialPalSpecifier = initial;
                if (initial.LatestJob != null && initial.LatestJob.Results == null && initial.LatestJob.CurrentState != SolverState.Idle)
                {
                    CurrentPalSpecifier = initial.LatestJob.Specifier;
                }
                else
                {
                    CurrentPalSpecifier = initial.Copy();
                }

                PalSource = new PalSourceTreeViewModel(sourceSave.CachedValue);
            }

            // Subscribe to PropertyChanged BEFORE we restore selections, but keep it suppressed
            PalSource.PropertyChanged += PalSource_PropertyChanged;

            // Log the selections before attempting to restore them
            if (CurrentPalSpecifier.PalSourceSelections?.Any() == true)
            {
                logger.Information("PalTargetViewModel: Attempting to restore {count} selections", CurrentPalSpecifier.PalSourceSelections.Count);
                foreach (var sel in CurrentPalSpecifier.PalSourceSelections)
                {
                    logger.Information("  - Selection: {id}", sel.SerializedId);
                }
            }

            // Temporarily suppress PropertyChanged events to prevent overwriting selections
            suppressPalSourcePropertyChanged = true;
            try
            {
                if (CurrentPalSpecifier.PalSourceSelections?.Any() == true)
                {
                    PalSource.Selections = CurrentPalSpecifier.PalSourceSelections;
                    
                    // Log the selections after setting them
                    logger.Information("PalTargetViewModel: After setting PalSource.Selections, got {count} selections", PalSource.Selections?.Count ?? 0);
                    if (PalSource.Selections != null)
                    {
                        foreach (var sel in PalSource.Selections)
                        {
                            logger.Information("  - Actual selection: {id}", sel.SerializedId);
                        }
                    }
                }
                
                // Keep suppression active while we refresh
                if (PalSource.Selections != null)
                    CurrentPalSpecifier.RefreshWith(AvailablePals);
            }
            finally
            {
                suppressPalSourcePropertyChanged = false;
            }

            void RefreshOnChange(object sender, PropertyChangedEventArgs ev)
            {
                CurrentPalSpecifier?.RefreshWith(AvailablePals);
            }

            PropertyChangedEventManager.AddHandler(sourceSave.Customizations, RefreshOnChange, nameof(sourceSave.Customizations.CustomContainers));

            PropertyChangedEventManager.AddHandler(CurrentPalSpecifier, RefreshOnChange, nameof(CurrentPalSpecifier.IncludeCagedPals));
            PropertyChangedEventManager.AddHandler(CurrentPalSpecifier, RefreshOnChange, nameof(CurrentPalSpecifier.IncludeBasePals));
            PropertyChangedEventManager.AddHandler(CurrentPalSpecifier, RefreshOnChange, nameof(CurrentPalSpecifier.IncludeCustomPals));
            PropertyChangedEventManager.AddHandler(CurrentPalSpecifier, RefreshOnChange, nameof(CurrentPalSpecifier.IncludeGlobalStoragePals));
            
            Presets = presets;
            OpenPresetsMenuCommand = new RelayCommand(() => PresetsMenuIsOpen = true);

            presets.PresetSelected += (_) => PresetsMenuIsOpen = false;

            OpenPassivesSearchCommand = new RelayCommand(() => new PassivesSearchWindow() { Owner = App.Current.MainWindow }.Show());
        }

        private void PalSource_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (suppressPalSourcePropertyChanged)
            {
                logger.Information("PalSource_PropertyChanged: Suppressed (property: {propertyName})", e.PropertyName);
                return;
            }

            if (e.PropertyName == nameof(PalSource.HasValidSource))
                OnPropertyChanged(nameof(IsValid));

            if (e.PropertyName == nameof(PalSource.Selections) && PalSource.Selections?.Any() == true)
            {
                logger.Information("PalSource_PropertyChanged: Updating CurrentPalSpecifier.PalSourceSelections with {count} selections", PalSource.Selections.Count);
                foreach (var sel in PalSource.Selections)
                {
                    logger.Information("  - Selection from PalSource: {id}", sel.SerializedId);
                }
                
                CurrentPalSpecifier.PalSourceSelections = PalSource.Selections;
                CurrentPalSpecifier?.RefreshWith(AvailablePals);
            }
        }

        private SolverJobViewModel currentLatestJob;
        public SolverJobViewModel CurrentLatestJob
        {
            get => currentLatestJob;
            private set
            {
                if (currentLatestJob != null && currentLatestJob != value)
                {
                    currentLatestJob.PropertyChanged -= CurrentLatestJob_PropertyChanged;
                }

                if (SetProperty(ref currentLatestJob, value))
                {
                    OnPropertyChanged(nameof(CanEdit));

                    if (value != null)
                    {
                        value.PropertyChanged += CurrentLatestJob_PropertyChanged;
                    }
                }
            }
        }

        private void CurrentLatestJob_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CurrentLatestJob.IsActive))
                OnPropertyChanged(nameof(CanEdit));
        }

        public bool CanEdit => CurrentLatestJob == null || !CurrentLatestJob.IsActive;

        [ObservableProperty]
        private PalSpecifierViewModel initialPalSpecifier;

        private PalSpecifierViewModel currentPalSpecifier;
        public PalSpecifierViewModel CurrentPalSpecifier
        {
            get => currentPalSpecifier;
            private set
            {
                var oldValue = CurrentPalSpecifier;
                if (SetProperty(ref currentPalSpecifier, value))
                {
                    if (oldValue != null) oldValue.PropertyChanged -= CurrentSpec_PropertyChanged;

                    value.PropertyChanged += CurrentSpec_PropertyChanged;
                    OnPropertyChanged(nameof(IsValid));
                    OnPropertyChanged(nameof(CanEdit));

                    CurrentLatestJob = value?.LatestJob;

                    if (value != null)
                    {
                        value?.RefreshWith(AvailablePals);
                    }
                }
            }
        }

        private void CurrentSpec_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(CurrentPalSpecifier.IsValid):
                    OnPropertyChanged(nameof(IsValid));
                    break;

                case nameof(CurrentPalSpecifier.LatestJob):
                    CurrentLatestJob = CurrentPalSpecifier.LatestJob;
                    break;

                case nameof(CurrentPalSpecifier.IncludeBasePals):
                case nameof(CurrentPalSpecifier.IncludeCagedPals):
                case nameof(CurrentPalSpecifier.IncludeCustomPals):
                    CurrentPalSpecifier.RefreshWith(AvailablePals);
                    break;
            }
        }

        public bool IsValid => PalSource.HasValidSource && CurrentPalSpecifier.IsValid;

        public PalSourceTreeViewModel PalSource { get; set; }

        public PassiveSkillsPresetCollectionViewModel Presets { get; }

        public IEnumerable<PalInstance> AvailablePals
        {
            get
            {
                if (PalSource?.Selections != null)
                {
                    var cachedSave = sourceSave.CachedValue;
                    var selections = PalSource.Selections;
                    foreach (var pal in cachedSave.OwnedPals)
                    {
                        if (!selections.Any(s => s.Matches(cachedSave, pal)))
                            continue;

                        if (pal.Location.Type == LocationType.GlobalPalStorage)
                            continue;

                        if (!CurrentPalSpecifier.IncludeBasePals && pal.Location.Type == LocationType.Base)
                            continue;

                        if (!CurrentPalSpecifier.IncludeCagedPals && pal.Location.Type == LocationType.ViewingCage)
                            continue;

                        if (!CurrentPalSpecifier.IncludeExpeditionPals && pal.IsOnExpedition)
                            continue;

                        yield return pal;
                    }
                }

                if (CurrentPalSpecifier.IncludeGlobalStoragePals)
                {
                    foreach (var pal in sourceSave.CachedValue.OwnedPals.Where(p => p.Location.Type == LocationType.GlobalPalStorage))
                    {
                        yield return pal;
                    }
                }

                if (CurrentPalSpecifier.IncludeCustomPals)
                {
                    foreach (var pal in sourceSave.Customizations.CustomContainers.SelectMany(c => c.Contents))
                        if (pal.IsValid)
                            yield return pal.ModelObject;
                }
            }
        }

        [ObservableProperty]
        private bool presetsMenuIsOpen = false;

        public IRelayCommand OpenPresetsMenuCommand { get; }

        public IRelayCommand OpenPassivesSearchCommand { get; }
    }
}
