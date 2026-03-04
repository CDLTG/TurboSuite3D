#nullable disable
using System;
using TurboSuite.Zones.ViewModels;

namespace TurboSuite.Zones.Models
{
    public class ProcessorLink : ViewModelBase
    {
        public const int MaxDevices = 99;
        public const int MaxLoads = 512;

        private int _usedDevices;
        private int _usedLoads;
        private string _linkType = "QS";

        public string ProcessorPanelName { get; set; }
        public int LinkNumber { get; set; }

        public string LinkType
        {
            get => _linkType;
            set
            {
                if (SetProperty(ref _linkType, value))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }

        public bool IsClearConnect => string.Equals(_linkType, "Clear Connect Type A", StringComparison.OrdinalIgnoreCase);

        public string DisplayName => $"Link {LinkNumber} ({_linkType})";

        public int UsedDevices
        {
            get => _usedDevices;
            set
            {
                if (SetProperty(ref _usedDevices, value))
                {
                    OnPropertyChanged(nameof(DevicePercent));
                    OnPropertyChanged(nameof(IsOverDeviceCapacity));
                }
            }
        }

        public int UsedLoads
        {
            get => _usedLoads;
            set
            {
                if (SetProperty(ref _usedLoads, value))
                {
                    OnPropertyChanged(nameof(LoadPercent));
                    OnPropertyChanged(nameof(IsOverLoadCapacity));
                }
            }
        }

        public double DevicePercent => MaxDevices > 0 ? (double)_usedDevices / MaxDevices : 0;
        public double LoadPercent => MaxLoads > 0 ? (double)_usedLoads / MaxLoads : 0;
        public bool IsOverDeviceCapacity => _usedDevices > MaxDevices;
        public bool IsOverLoadCapacity => _usedLoads > MaxLoads;
    }
}
