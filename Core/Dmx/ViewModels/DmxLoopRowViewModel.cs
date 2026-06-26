#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>One Control Zone as a tickable member of a loop (Zone→Loop, Q9).</summary>
    public sealed class DmxZoneAssignmentViewModel : ViewModelBase
    {
        private bool _isAssigned;

        public DmxZoneAssignmentViewModel(string zoneName, bool isAssigned = false)
        {
            ZoneName = zoneName;
            _isAssigned = isAssigned;
        }

        public string ZoneName { get; }

        public bool IsAssigned
        {
            get => _isAssigned;
            set => SetProperty(ref _isAssigned, value);
        }
    }

    /// <summary>
    /// A designer-declared DMX Loop in the builder (TurboDMX-UI-Structure §1 "Loops +"): a name plus the
    /// Control Zones ticked into it. Maps to an engine <see cref="LoopDeclaration"/>; assigning more
    /// channels than the interface ceiling is the engine's third pre-solve gate, surfaced on Run.
    /// </summary>
    public sealed class DmxLoopRowViewModel : ViewModelBase
    {
        private string _name;

        public DmxLoopRowViewModel(string name, IEnumerable<string> allZoneNames, IEnumerable<string>? assigned = null)
        {
            _name = name;
            var assignedSet = new HashSet<string>(assigned ?? new string[0], System.StringComparer.OrdinalIgnoreCase);
            Zones = new ObservableCollection<DmxZoneAssignmentViewModel>(
                allZoneNames.Select(z => new DmxZoneAssignmentViewModel(z, assignedSet.Contains(z))));
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public ObservableCollection<DmxZoneAssignmentViewModel> Zones { get; }

        /// <summary>The ticked zone names, in list order — the loop's chain order.</summary>
        public IReadOnlyList<string> AssignedZoneNames =>
            Zones.Where(z => z.IsAssigned).Select(z => z.ZoneName).ToList();

        /// <summary>Convert to the engine declaration (null if the loop has no zones ticked — skip it).</summary>
        public LoopDeclaration? ToDeclaration()
        {
            var zones = AssignedZoneNames;
            return zones.Count == 0 ? null : new LoopDeclaration(Name, zones);
        }
    }
}
