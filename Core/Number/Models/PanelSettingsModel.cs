#nullable disable
using TurboSuite.Abstractions;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.Models
{
    public class PanelSettingsModel : ViewModelBase
    {
        private string _circuitNaming;
        private string _circuitPrefix;
        private string _circuitPrefixSeparator;

        public string PanelName { get; }
        public ElementRef PanelElementId { get; }

        public string CircuitNaming
        {
            get => _circuitNaming;
            set => SetProperty(ref _circuitNaming, value);
        }

        public string CircuitPrefix
        {
            get => _circuitPrefix;
            set => SetProperty(ref _circuitPrefix, value);
        }

        public string CircuitPrefixSeparator
        {
            get => _circuitPrefixSeparator;
            set => SetProperty(ref _circuitPrefixSeparator, value);
        }

        public PanelSettingsModel(string panelName, ElementRef panelElementId,
            string circuitNaming, string circuitPrefix, string circuitPrefixSeparator)
        {
            PanelName = panelName;
            PanelElementId = panelElementId;
            _circuitNaming = circuitNaming ?? "(None)";
            _circuitPrefix = circuitPrefix ?? "";
            _circuitPrefixSeparator = circuitPrefixSeparator ?? "";
        }
    }
}
