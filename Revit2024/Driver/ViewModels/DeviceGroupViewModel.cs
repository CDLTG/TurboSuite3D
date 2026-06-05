#nullable disable
using System.Collections.ObjectModel;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// ViewModel for grouping devices by family type
    /// </summary>
    public class DeviceGroupViewModel : ViewModelBase
    {
        private string _familyTypeName;

        public string FamilyTypeName
        {
            get => _familyTypeName;
            set => SetProperty(ref _familyTypeName, value);
        }

        public ObservableCollection<LightingDeviceViewModel> Devices { get; set; }

        public DeviceGroupViewModel(string familyTypeName)
        {
            _familyTypeName = familyTypeName;
            Devices = new ObservableCollection<LightingDeviceViewModel>();
        }
    }
}
