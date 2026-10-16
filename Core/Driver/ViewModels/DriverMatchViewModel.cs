#nullable disable
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// TurboDriver match-check ViewModel. Holds the two type lists, the current picks, and the
    /// resulting comparison rows. The comparison is pure in-memory Core
    /// (<see cref="DriverMatchDiagnostic"/>) recomputed on every pick — no Revit call. Only
    /// <see cref="Reload"/> touches the model, through the work queue (collection on the API thread).
    /// </summary>
    public class DriverMatchViewModel : ViewModelBase
    {
        private readonly IDriverMatchOperations _ops;
        private readonly IRevitWorkQueue _workQueue;

        private DriverOption _selectedDriver;
        private FixtureOption _selectedFixture;
        private string _summary;
        private bool _matches;
        private bool _hasResult;
        private bool _isBusy;
        private string _status;

        public DriverMatchViewModel(MatchCatalog catalog, IDriverMatchOperations ops, IRevitWorkQueue workQueue)
        {
            _ops = ops;
            _workQueue = workQueue;

            Drivers = new ObservableCollection<DriverOption>();
            Fixtures = new ObservableCollection<FixtureOption>();
            Checks = new ObservableCollection<MatchCheck>();

            ReloadCommand = new RelayCommand(Reload, () => !_isBusy);

            Load(catalog);
        }

        public ObservableCollection<DriverOption> Drivers { get; }
        public ObservableCollection<FixtureOption> Fixtures { get; }
        public ObservableCollection<MatchCheck> Checks { get; }

        public ICommand ReloadCommand { get; }

        public DriverOption SelectedDriver
        {
            get => _selectedDriver;
            set { if (SetProperty(ref _selectedDriver, value)) Recompute(); }
        }

        public FixtureOption SelectedFixture
        {
            get => _selectedFixture;
            set { if (SetProperty(ref _selectedFixture, value)) Recompute(); }
        }

        /// <summary>Plain-language one-line verdict for the summary bar.</summary>
        public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

        /// <summary>True when the current pair matches (drives the green/red summary bar).</summary>
        public bool Matches { get => _matches; private set => SetProperty(ref _matches, value); }

        /// <summary>True once both a driver and a fixture are picked (bar is neutral until then).</summary>
        public bool HasResult { get => _hasResult; private set => SetProperty(ref _hasResult, value); }

        /// <summary>Footer line: how many types are loaded.</summary>
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        public bool IsBusy
        {
            get => _isBusy;
            private set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        private void Load(MatchCatalog catalog)
        {
            Drivers.Clear();
            Fixtures.Clear();
            if (catalog != null)
            {
                foreach (var d in catalog.Drivers) Drivers.Add(d);
                foreach (var f in catalog.Fixtures) Fixtures.Add(f);
            }
            Status = $"{Drivers.Count} driver type(s) · {Fixtures.Count} RPS fixture type(s)";
            Recompute();
        }

        private void Recompute()
        {
            Checks.Clear();
            if (_selectedDriver == null || _selectedFixture == null)
            {
                HasResult = false;
                Matches = false;
                Summary = "Select a driver and an RPS fixture to compare.";
                return;
            }

            var report = DriverMatchDiagnostic.Compare(_selectedFixture.Fixture, _selectedDriver.Driver);
            foreach (var c in report.Checks) Checks.Add(c);
            Matches = report.Matches;
            Summary = report.Summary;
            HasResult = true;
        }

        private void Reload()
        {
            IsBusy = true;
            // Keep the current picks across the reload (families may have been reloaded/renamed).
            string driverName = _selectedDriver?.DisplayName;
            string fixtureName = _selectedFixture?.DisplayName;

            _workQueue.Enqueue(
                () => _ops.Collect(),
                result =>
                {
                    try
                    {
                        if (result is MatchCatalog cat)
                        {
                            Load(cat);
                            SelectedDriver = Drivers.FirstOrDefault(d => d.DisplayName == driverName);
                            SelectedFixture = Fixtures.FirstOrDefault(f => f.DisplayName == fixtureName);
                        }
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }
    }
}
