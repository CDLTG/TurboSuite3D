#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>One declared cluster within a zone (§8d): a name + the fixture (run) ElementIds bound to it.
    /// "Verify" highlights its runs in the model; "Add selection" folds the current model selection into it
    /// (last-wins reassign); "✕" removes it (its runs fall back to the zone's residual).</summary>
    public sealed class DmxClusterRowViewModel : ViewModelBase
    {
        private string _name;

        public DmxClusterRowViewModel(string clusterId, string name, IEnumerable<long> runIds)
        {
            ClusterId = clusterId;
            _name = name;
            RunIds = new List<long>(runIds);
        }

        public string ClusterId { get; }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>Mutable so the owning group can reassign/remove runs across clusters before re-solving.</summary>
        public List<long> RunIds { get; }

        public int RunCount => RunIds.Count;

        public ICommand? VerifyCommand { get; set; }
        public ICommand? AddSelectionCommand { get; set; }
        public ICommand? RemoveCommand { get; set; }

        public void RaiseRunCountChanged() => OnPropertyChanged(nameof(RunCount));
    }

    /// <summary>One zone's cluster editor (§8d): its declared clusters plus the visible "(unclustered)"
    /// residual count. Only shown for zones the designer chooses to split — the flat default needs no UI.</summary>
    public sealed class DmxZoneClusterGroupViewModel : ViewModelBase
    {
        public DmxZoneClusterGroupViewModel(string zoneName, int totalRuns)
        {
            ZoneName = zoneName;
            TotalRuns = totalRuns;
            Clusters = new ObservableCollection<DmxClusterRowViewModel>();
        }

        public string ZoneName { get; }
        public int TotalRuns { get; }

        public ObservableCollection<DmxClusterRowViewModel> Clusters { get; }

        /// <summary>Runs not bound to any cluster — shown as the residual, never a blocking error.</summary>
        public int ResidualCount => TotalRuns - Clusters.Sum(c => c.RunCount);

        public string ResidualText => $"(unclustered): {ResidualCount}";

        /// <summary>True once the zone has ≥1 declared cluster (so it packs per cluster, not flat).</summary>
        public bool HasClusters => Clusters.Count > 0;

        public ICommand? NewClusterCommand { get; set; }

        public void RaiseResidualChanged()
        {
            OnPropertyChanged(nameof(ResidualCount));
            OnPropertyChanged(nameof(ResidualText));
            OnPropertyChanged(nameof(HasClusters));
        }
    }
}
