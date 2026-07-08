#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>One declared cluster within a zone: a name + the fixture (run) ElementIds bound to it.
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

        /// <summary>Mutable so the owning zone can reassign/remove runs across clusters before re-solving.</summary>
        public List<long> RunIds { get; }

        public int RunCount => RunIds.Count;

        private int _bundleCount;

        /// <summary>How many bundles (field-connectable chains) this cluster's fixtures coalesce into —
        /// the count the packer actually sees. Set by the owner after the readings are resolved.</summary>
        public int BundleCount
        {
            get => _bundleCount;
            set { if (SetProperty(ref _bundleCount, value)) OnPropertyChanged(nameof(BundleText)); }
        }

        /// <summary>"→ N bundles", shown only when bundling actually collapses runs (a bundle-aware
        /// product). A max-1 product bundles 1:1, so this stays blank — no noise on ordinary fixtures.</summary>
        public string BundleText => BundleCount > 0 && BundleCount < RunCount ? $"→ {BundleCount} bundles" : "";

        public ICommand? VerifyCommand { get; set; }
        public ICommand? RemoveCommand { get; set; }

        public void RaiseRunCountChanged()
        {
            OnPropertyChanged(nameof(RunCount));
            OnPropertyChanged(nameof(BundleText));
        }
    }

    /// <summary>
    /// One zone as it lives inside a loop (the loop-centric tree's middle tier): the zone name + run count,
    /// a "← (to pool)" action to unassign it, and — for a location-spanning zone (≥2 runs) — its nested
    /// cluster sub-builder with the visible "(unclustered)" residual. A single-run zone shows no
    /// cluster UI (the flat default needs none). Clusters are keyed by zone value in ExtensibleStorage,
    /// independent of which loop the zone sits in, so moving a zone pool→loop→pool preserves them.
    /// </summary>
    public sealed class DmxLoopZoneViewModel : ViewModelBase
    {
        public DmxLoopZoneViewModel(string zoneName, int totalRuns)
        {
            ZoneName = zoneName;
            TotalRuns = totalRuns;
            Clusters = new ObservableCollection<DmxClusterRowViewModel>();
        }

        public string ZoneName { get; }
        public int TotalRuns { get; }

        private int _bundleCount;

        /// <summary>The zone's total bundle count — the sum of its clusters' (and residual's) bundles, or
        /// the whole-zone bundle count when flat. Set by the owner after the readings resolve; drives the
        /// header's "→ N bundles" suffix. See <see cref="HasBundles"/> for the gate.</summary>
        public int BundleCount
        {
            get => _bundleCount;
            set { if (SetProperty(ref _bundleCount, value)) OnPropertyChanged(nameof(Header)); }
        }

        /// <summary>True when bundling actually reduces the count (a bundle-aware product is present). A
        /// max-1 product bundles 1:1, so the header stays a plain run count with no bundle suffix.</summary>
        private bool HasBundles => BundleCount > 0 && BundleCount < TotalRuns;

        public string Header => HasBundles
            ? $"{ZoneName}  ({TotalRuns} runs → {BundleCount} bundles)"
            : $"{ZoneName}  ({TotalRuns} run{(TotalRuns == 1 ? "" : "s")})";

        /// <summary>Only a zone with ≥2 runs can be split — single-run zones never show the cluster sub-builder.</summary>
        public bool CanSplit => TotalRuns >= 2;

        public ObservableCollection<DmxClusterRowViewModel> Clusters { get; }

        /// <summary>Runs not bound to any cluster — shown as the residual, never a blocking error.</summary>
        public int ResidualCount => TotalRuns - Clusters.Sum(c => c.RunCount);

        public string ResidualText => $"(unclustered): {ResidualCount}";

        /// <summary>True once the zone has ≥1 declared cluster (so it packs per cluster, not flat).</summary>
        public bool HasClusters => Clusters.Count > 0;

        public ICommand? NewClusterCommand { get; set; }       // + from selection
        public ICommand? RemoveFromLoopCommand { get; set; }   // ← return this zone to the pool

        public void RaiseResidualChanged()
        {
            OnPropertyChanged(nameof(ResidualCount));
            OnPropertyChanged(nameof(ResidualText));
            OnPropertyChanged(nameof(HasClusters));
        }
    }
}
