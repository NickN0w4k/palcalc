using CommunityToolkit.Mvvm.ComponentModel;
using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.UI.Localization;
using PalCalc.UI.Model;
using PalCalc.UI.ViewModel.Mapped;
using QuickGraph;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace PalCalc.UI.ViewModel.Solver
{
    public interface IPalSourceTreeNode : INotifyPropertyChanged
    {
        public ILocalizedText Label { get; }

        public bool? IsChecked { get; set; }

        public IEnumerable<IPalSourceTreeNode> Children { get; }

        /// <summary>
        /// Returns the current state of this node as a selection, which will also encompass any child-node selections.
        /// </summary>
        public List<IPalSourceTreeSelection> AsSelection { get; }

        /// <summary>
        /// Updates the state of this node, and its children, so it matches the given selections.
        /// </summary>
        public void ReadFromSelections(List<IPalSourceTreeSelection> selections);
    }

    
    public partial class PlayerSourceTreeNodeViewModel(PlayerInstance player) : ObservableObject, IPalSourceTreeNode
    {
        public PlayerInstance ModelObject => player;

        public ILocalizedText Label { get; } = new HardCodedText(player.Name);

        public IEnumerable<IPalSourceTreeNode> Children => [];

        [ObservableProperty]
        private bool? isChecked = true;

        public List<IPalSourceTreeSelection> AsSelection => IsChecked == true ? [new SourceTreePlayerSelection(player)] : [];

        public void ReadFromSelections(List<IPalSourceTreeSelection> selections)
        {
            var directSelection = selections.OfType<SourceTreePlayerSelection>().Any(s => s.ModelObject.PlayerId == player.PlayerId);
            var allItemsSelection = selections.OfType<SourceTreeAllSelection>().Any();

            IsChecked = directSelection || allItemsSelection;
        }
    }


    public partial class PalSourceTreeViewModel : ObservableObject
    {
        private static ILogger logger = Log.ForContext<PalSourceTreeViewModel>();
        private bool suppressSelectionChanged = false;
        private void SuppressSelectionChangedDuring(Action fn)
        {
            suppressSelectionChanged = true;
            try { fn(); }
            finally { suppressSelectionChanged = false; }
        }

        // for XAML designer view
        public PalSourceTreeViewModel() : this(CachedSaveGame.SampleForDesignerView)
        {

        }

        public CachedSaveGame Save { get; }

        public PalSourceTreeViewModel(CachedSaveGame save)
        {
            Save = save;

            RootNodes = save.Players
                .OrderBy(p => p.Name)
                .Select(p => new PlayerSourceTreeNodeViewModel(p))
                .OfType<IPalSourceTreeNode>()
                .ToList();

            // only subscribe to changes in root nodes for raising change-events, try to avoid massive event
            // cascades/re-triggering
            //
            // assume root nodes will raise events appropriately if children change
            foreach (var node in RootNodes)
            {
                PropertyChangedEventManager.AddHandler(node, Node_SelectionPropertyChanged, nameof(node.AsSelection));
            }
            
            logger.Information("PalSourceTreeViewModel: Created with {count} players (all initially checked)", RootNodes.Count);
        }

        private void Node_SelectionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!suppressSelectionChanged)
            {
                OnPropertyChanged(nameof(Selections));
                OnPropertyChanged(nameof(HasValidSource));
            }
        }

        public List<IPalSourceTreeSelection> Selections
        {
            get
            {
                var selections = AllNodes.All(n => n.IsChecked == true)
                    ? [new SourceTreeAllSelection()]
                    : RootNodes.SelectMany(n => n.AsSelection).ToList();
                
                return selections;
            }
            set
            {
                logger.Information("PalSourceTreeViewModel.Selections setter: Received {count} selections", value?.Count ?? 0);
                if (value != null)
                {
                    foreach (var sel in value)
                    {
                        logger.Information("  - Setting selection: {id}", sel.SerializedId);
                    }
                }
                
                SuppressSelectionChangedDuring(() =>
                {
                    if (value.OfType<SourceTreeAllSelection>().Any())
                    {
                        logger.Information("PalSourceTreeViewModel.Selections setter: Setting all nodes to checked");
                        foreach (var node in AllNodes)
                            node.IsChecked = true;
                    }
                    else
                    {
                        logger.Information("PalSourceTreeViewModel.Selections setter: Applying selections to root nodes");
                        // First, uncheck all nodes
                        foreach (var node in AllNodes)
                            node.IsChecked = false;
                        
                        // Then apply the specific selections
                        foreach (var node in RootNodes)
                            node.ReadFromSelections(value);
                    }
                    
                    // Raise PropertyChanged events INSIDE the suppression block won't help
                    // because the events are queued and fired after
                });
                
                // These property changed notifications happen AFTER suppressSelectionChanged is set back to false
                OnPropertyChanged(nameof(Selections));
                OnPropertyChanged(nameof(HasValidSource));
                
                // Log what the getter returns after setting
                var currentSelections = Selections;
                logger.Information("PalSourceTreeViewModel.Selections setter: After setting, getter returns {count} selections", currentSelections.Count);
                foreach (var sel in currentSelections)
                {
                    logger.Information("  - Current selection: {id}", sel.SerializedId);
                }
            }
        }

        public bool HasValidSource => Selections.Any();

        public List<IPalSourceTreeNode> RootNodes { get; }

        private IEnumerable<IPalSourceTreeNode> AllNodes
        {
            get
            {
                IEnumerable<IPalSourceTreeNode> Enumerate(IPalSourceTreeNode node)
                {
                    yield return node;

                    foreach (var child in node.Children.SelectMany(Enumerate))
                    {
                        yield return child;
                    }
                }

                return RootNodes.SelectMany(Enumerate);
            }
        }
    }
}
