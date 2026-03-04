#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Driver.ViewModels
{
    public class GroupedFixture
    {
        public int Quantity { get; set; }
        public string TypeMark { get; set; }
        public string Comments { get; set; }
        public double LinearLength { get; set; }
    }

    /// <summary>
    /// ViewModel for electrical circuit with calculated totals
    /// </summary>
    public class CircuitViewModel : ViewModelBase
    {
        private readonly CircuitData _data;
        private readonly List<DriverCandidateInfo> _driverCandidates;
        private double _totalLinearLength;
        private DriverRecommendation _driverRecommendation;

        public CircuitData Data => _data;

        public string CircuitNumber => _data.CircuitNumber;
        public string LoadName => _data.LoadName;
        public string LoadClassificationAbbreviation => _data.LoadClassificationAbbreviation;
        public int NumberOfElements => _data.NumberOfElements;
        public double ApparentPower => _data.ApparentPower;
        public string Panel => _data.Panel;

        public ObservableCollection<LightingFixtureViewModel> Fixtures { get; set; }
        public ObservableCollection<DeviceGroupViewModel> DeviceGroups { get; set; }

        public double TotalLinearLength
        {
            get => _totalLinearLength;
            set => SetProperty(ref _totalLinearLength, value);
        }

        public List<GroupedFixture> GroupedFixtures => _data.LightingFixtures
            .GroupBy(f => new { f.TypeMark, f.Comments, LinearLength = Math.Round(f.LinearLength, 4) })
            .Select(g => new GroupedFixture
            {
                Quantity = g.Count(),
                TypeMark = g.Key.TypeMark,
                Comments = g.Key.Comments,
                LinearLength = g.Key.LinearLength
            })
            .OrderBy(g => g.TypeMark)
            .ToList();

        public int TotalFixtureCount => _data.LightingFixtures.Count;

        public DriverRecommendation DriverRecommendation
        {
            get => _driverRecommendation;
            set
            {
                if (SetProperty(ref _driverRecommendation, value))
                {
                    OnPropertyChanged(nameof(HasDriverMatch));
                    OnPropertyChanged(nameof(DriverWarning));
                }
            }
        }

        public bool HasDriverMatch => DriverRecommendation?.HasMatch ?? false;

        public string DriverWarning => DriverRecommendation?.WarningMessage ?? "";

        public CircuitViewModel(CircuitData data, List<DriverCandidateInfo> driverCandidates)
        {
            _data = data;
            _driverCandidates = driverCandidates;

            Fixtures = new ObservableCollection<LightingFixtureViewModel>();
            DeviceGroups = new ObservableCollection<DeviceGroupViewModel>();

            foreach (var fixture in data.LightingFixtures)
            {
                Fixtures.Add(new LightingFixtureViewModel(fixture));
            }

            CalculateTotals();
            CalculateDriverRecommendation();
        }

        public void CalculateTotals()
        {
            TotalLinearLength = CalculationHelper.CalculateTotalLinearLength(_data.LightingFixtures);
        }

        private void CalculateDriverRecommendation()
        {
            var service = new DriverSelectionService();
            DriverRecommendation = service.GetRecommendation(_data.LightingFixtures, _driverCandidates);
        }

        public void RebuildDeviceGroups()
        {
            DeviceGroups.Clear();

            foreach (var kvp in _data.DevicesByType.OrderBy(x => x.Key))
            {
                DeviceGroupViewModel group = new DeviceGroupViewModel(kvp.Key);
                DeviceGroups.Add(group);
            }
        }
    }
}
