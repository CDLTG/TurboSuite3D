#nullable disable
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// Shell ViewModel for the TurboRPS window's two tabs: the deploy-time circuit staleness
    /// dashboard (<see cref="Circuits"/>) and the design-time driver↔fixture match check
    /// (<see cref="Match"/>). The two share the one <c>IRevitWorkQueue</c> the command creates. The
    /// shell's one piece of wiring: a link change on Driver Match re-scans the Circuits tab (the queue
    /// is a FIFO, so the rescan runs after the link save).
    /// </summary>
    public class TurboRpsShellViewModel : ViewModelBase
    {
        public TurboRpsShellViewModel(RpsMainViewModel circuits, DriverMatchViewModel match)
        {
            Circuits = circuits;
            Match = match;
            if (circuits != null && match != null)
                match.LinksChanged += (_, _) => circuits.RequestRescan();
        }

        public RpsMainViewModel Circuits { get; }
        public DriverMatchViewModel Match { get; }
    }
}
