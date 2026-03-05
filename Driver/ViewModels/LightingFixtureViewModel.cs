#nullable disable
using TurboSuite.Driver.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// ViewModel for lighting fixture display
    /// </summary>
    public class LightingFixtureViewModel : ViewModelBase
    {
        private readonly FixtureData _data;

        public FixtureData Data => _data;

        public string TypeMark => _data.TypeMark;
        public double LinearLength => _data.LinearLength;
        public double LinearPower => _data.LinearPower;

        public LightingFixtureViewModel(FixtureData data)
        {
            _data = data;
        }
    }
}
