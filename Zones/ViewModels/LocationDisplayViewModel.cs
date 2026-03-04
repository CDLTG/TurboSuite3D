#nullable disable
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.ViewModels
{
    public class LocationDisplayViewModel : ViewModelBase
    {
        public LocationResult Location { get; set; }
        public bool IsLastLocation { get; set; }
    }
}
