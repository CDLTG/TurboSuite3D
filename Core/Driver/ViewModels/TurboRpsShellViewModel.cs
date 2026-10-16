#nullable disable
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// Shell ViewModel for the TurboRPS window's two tabs: the deploy-time circuit staleness
    /// dashboard (<see cref="Circuits"/>) and the design-time driver↔fixture match check
    /// (<see cref="Match"/>). Pure composition — each child VM is unchanged, and the two share the one
    /// <c>IRevitWorkQueue</c> the command creates; the shell only groups them for the TabControl.
    /// </summary>
    public class TurboRpsShellViewModel : ViewModelBase
    {
        public TurboRpsShellViewModel(RpsMainViewModel circuits, DriverMatchViewModel match)
        {
            Circuits = circuits;
            Match = match;
        }

        public RpsMainViewModel Circuits { get; }
        public DriverMatchViewModel Match { get; }
    }
}
