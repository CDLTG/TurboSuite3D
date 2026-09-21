#nullable disable
using System;
using System.Collections.Generic;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.ViewModels
{
    /// <summary>
    /// One location column in the Panel Breakdown, plus the orphan-assignment affordance (plan item 5):
    /// a header hint and, for an orphan location, a dropdown to assign it to a processor-bearing pool.
    /// A thin DTO rebuilt every allocation pass; the hint is a read-only derived string and the only
    /// state is the map the owning <c>PanelBreakdownTabViewModel</c> holds and persists.
    /// </summary>
    public class LocationDisplayViewModel : ViewModelBase
    {
        private string _locationHint = "";
        private OrphanTargetOption _selectedOrphanTarget;
        private bool _suppressAssign;
        private bool _isOrphan;
        private List<OrphanTargetOption> _orphanTargets = new List<OrphanTargetOption>();

        public LocationResult Location { get; set; }
        public bool IsLastLocation { get; set; }

        /// <summary>The compact orphan-state tag shown on the pool dropdown in the header: "→ ?" while
        /// an orphan has no pool, or "→ 1" once assigned to Location 1 — short so it does not widen the
        /// column. Empty for a normal location, where no dropdown shows. The popup list carries the full
        /// "Location N" labels.</summary>
        public string LocationHint
        {
            get => _locationHint;
            set => SetProperty(ref _locationHint, value);
        }

        /// <summary>True for an orphan location (panels but no processor) — the dropdown shows only then.
        /// Notifying, so a live processor add/remove can flip the dropdown in place without a full
        /// rebuild.</summary>
        public bool IsOrphan
        {
            get => _isOrphan;
            set => SetProperty(ref _isOrphan, value);
        }

        /// <summary>The pools this orphan can join: "— unassigned —" plus each processor-bearing
        /// location. Notifying so a live host add/remove updates the options.</summary>
        public List<OrphanTargetOption> OrphanTargets
        {
            get => _orphanTargets;
            set => SetProperty(ref _orphanTargets, value);
        }

        /// <summary>Raised when the user picks a pool: (orphanLocation, hostLocation-or-null). The owner
        /// updates and persists the map, then re-derives the arrangement.</summary>
        public Action<int, int?> OnAssign { get; set; }

        public OrphanTargetOption SelectedOrphanTarget
        {
            get => _selectedOrphanTarget;
            set
            {
                if (!SetProperty(ref _selectedOrphanTarget, value)) return;
                if (_suppressAssign || Location == null) return;
                OnAssign?.Invoke(Location.LocationNumber, value?.HostLocation);
            }
        }

        /// <summary>
        /// Applies the whole orphan state atomically, with <see cref="OnAssign"/> suppressed throughout —
        /// so replacing <see cref="OrphanTargets"/> on a live dropdown (which makes WPF null the now-absent
        /// <see cref="SelectedOrphanTarget"/> before we set the correct one) cannot fire a spurious
        /// assignment. Used both to build a row and to refresh one in place after a processor add/remove.
        /// </summary>
        public void UpdateOrphanState(
            bool isOrphan, List<OrphanTargetOption> targets, OrphanTargetOption selected,
            string hint, Action<int, int?> onAssign)
        {
            _suppressAssign = true;
            try
            {
                OnAssign = onAssign;
                IsOrphan = isOrphan;
                OrphanTargets = targets;          // ItemsSource swap may null SelectedItem — suppressed
                SelectedOrphanTarget = selected;  // then pin the correct option
                LocationHint = hint;
            }
            finally
            {
                _suppressAssign = false;
            }
        }
    }

    /// <summary>One choice in an orphan location's pool dropdown — a host location, or the unassigned
    /// sentinel (<see cref="HostLocation"/> null).</summary>
    public class OrphanTargetOption
    {
        public OrphanTargetOption(string label, int? hostLocation)
        {
            Label = label;
            HostLocation = hostLocation;
        }

        public string Label { get; }
        public int? HostLocation { get; }
    }
}
