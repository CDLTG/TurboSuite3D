#nullable enable
using System.Globalization;
using TurboSuite.Dmx.Input;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>One discovered decoder type in the curated-from-discovery checklist (Q10). Ticking it
    /// adds its caps to the contract's decoder pool.</summary>
    public sealed class DmxDecoderRowViewModel : ViewModelBase
    {
        private bool _isSelected;

        public DmxDecoderRowViewModel(DmxDecoderCandidate candidate, bool isSelected)
        {
            Candidate = candidate;
            _isSelected = isSelected;
        }

        public DmxDecoderCandidate Candidate { get; }
        public string Name => Candidate.Name;

        /// <summary>e.g. "4 out · 10 A/ch · 960 W".</summary>
        public string Detail =>
            string.Format(CultureInfo.InvariantCulture, "{0} out · {1:0.#} A/ch · {2:0} W",
                Candidate.MaxOutputs, Candidate.MaxAmpsPerOutput, Candidate.MaxWatts);

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>One discovered driver type in the curated checklist — same gesture as decoders.</summary>
    public sealed class DmxDriverRowViewModel : ViewModelBase
    {
        private bool _isSelected;

        public DmxDriverRowViewModel(DmxDriverCandidate candidate, bool isSelected)
        {
            Candidate = candidate;
            _isSelected = isSelected;
        }

        public DmxDriverCandidate Candidate { get; }
        public string Name => Candidate.Name;

        /// <summary>e.g. "288 W · 24 V · 80%".</summary>
        public string Detail =>
            string.Format(CultureInfo.InvariantCulture, "{0:0} W · {1:0.#} V · {2:0%}",
                Candidate.RatedWatts, Candidate.OperatingVolts,
                DeratingFactor.Normalize(Candidate.DeratingFactorRaw));

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
