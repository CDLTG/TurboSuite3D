#nullable disable
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Zones.ViewModels
{
    public class ZonesCircuitViewModel : ViewModelBase
    {
        private readonly ZonesCircuitData _data;
        private string _currentLoadName;

        public ZonesCircuitViewModel(ZonesCircuitData data)
        {
            _data = data;
            _currentLoadName = data.CurrentLoadName;
        }

        public ZonesCircuitData Data => _data;

        public string CircuitNumber => _data.CircuitNumber;
        public string DimmingType => _data.DimmingType;
        public string RoomName => _data.RoomName;
        public string FixtureComments => _data.FixtureComments;
        public string LoadClassificationName => _data.LoadClassificationName;
        public string UpdatedLoadName => _data.UpdatedLoadName;
        public bool IsFallbackLabel => _data.LabelSource == LabelSource.Fallback;

        public string CurrentLoadName
        {
            get => _currentLoadName;
            set => SetProperty(ref _currentLoadName, value);
        }

        public string RoomOverride
        {
            get => _data.RoomOverride;
            set
            {
                if (_data.RoomOverride == value) return;
                _data.RoomOverride = value;
                OnPropertyChanged();
                RecalculateUpdatedLoadName();
            }
        }

        public string CircuitComments
        {
            get => _data.CircuitComments;
            set
            {
                if (_data.CircuitComments == value) return;
                _data.CircuitComments = value;
                OnPropertyChanged();
                RecalculateUpdatedLoadName();
            }
        }

        private void RecalculateUpdatedLoadName()
        {
            string label = ZonesCollectorService.ResolveLabel(
                _data.CircuitComments, _data.FixtureComments, _data.LoadClassificationName, out LabelSource source);

            string room = !string.IsNullOrWhiteSpace(_data.RoomOverride)
                ? _data.RoomOverride
                : _data.RoomName;

            if (!string.IsNullOrWhiteSpace(room) && !string.IsNullOrWhiteSpace(label))
            {
                _data.UpdatedLoadName = $"{room.ToUpperInvariant()} - {label.ToLowerInvariant()}";
                _data.LabelSource = source;
            }
            else
            {
                _data.UpdatedLoadName = string.Empty;
                _data.LabelSource = LabelSource.None;
            }

            OnPropertyChanged(nameof(UpdatedLoadName));
            OnPropertyChanged(nameof(IsFallbackLabel));
        }
    }
}
