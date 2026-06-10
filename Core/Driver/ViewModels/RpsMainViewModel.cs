#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// TurboRPS dashboard ViewModel. Holds the per-circuit rows, the "show only issues"
    /// filter, live counts, and the four commands. All Revit work goes through the injected
    /// <see cref="IRevitWorkQueue"/> + <see cref="IRpsRevitOperations"/> (mirrors TurboZones).
    /// </summary>
    public class RpsMainViewModel : ViewModelBase
    {
        private readonly IRpsRevitOperations _ops;
        private readonly IRevitWorkQueue _workQueue;

        private bool _isBusy;
        private bool _showOnlyIssues;
        private string _searchText = string.Empty;
        private RpsCircuitRowViewModel _selectedRow;

        public RpsMainViewModel(IReadOnlyList<RpsCircuitData> circuits,
            IRpsRevitOperations ops, IRevitWorkQueue workQueue)
        {
            _ops = ops;
            _workQueue = workQueue;

            Rows = new ObservableCollection<RpsCircuitRowViewModel>();
            LoadRows(circuits);

            RowsView = CollectionViewSource.GetDefaultView(Rows);
            RowsView.Filter = FilterRow;

            SelectAllStaleCommand = new RelayCommand(SelectAllStale, () => !_isBusy);
            RescanCommand = new RelayCommand(Rescan, () => !_isBusy);
            ReRunSelectedCommand = new RelayCommand(ReRunSelected, CanReRun);
            SelectInProjectCommand = new RelayCommand(SelectInProject, CanSelectInProject);
            DeferSelectedCommand = new RelayCommand(DeferSelected, () => CanToggleDeferral && !_selectedRow.IsDeferred);
            ClearDeferralCommand = new RelayCommand(ClearDeferral, () => CanToggleDeferral && _selectedRow.IsDeferred);
        }

        public ObservableCollection<RpsCircuitRowViewModel> Rows { get; }
        public ICollectionView RowsView { get; }

        public ICommand SelectAllStaleCommand { get; }
        public ICommand RescanCommand { get; }
        public ICommand ReRunSelectedCommand { get; }
        public ICommand SelectInProjectCommand { get; }
        public ICommand DeferSelectedCommand { get; }
        public ICommand ClearDeferralCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool ShowOnlyIssues
        {
            get => _showOnlyIssues;
            set
            {
                if (SetProperty(ref _showOnlyIssues, value))
                    RowsView.Refresh();
            }
        }

        /// <summary>Live filter on the circuit number (substring, case-insensitive).</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                    RowsView.Refresh();
            }
        }

        public RpsCircuitRowViewModel SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (_selectedRow == value) return;
                if (_selectedRow != null) _selectedRow.IsActiveRow = false;
                if (SetProperty(ref _selectedRow, value))
                {
                    if (_selectedRow != null) _selectedRow.IsActiveRow = true;
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        // ---- Live counts (footer) ----
        // Issue counts exclude deferred rows — a deferred circuit is an accepted decision, not an
        // open issue. A drifted deferral surfaces under ReviewCount instead.
        public int TotalCount => Rows.Count;
        public int StaleCount => Rows.Count(r => r.Status == RpsStatus.Stale && !r.IsDeferred);
        public int RebuildCount => Rows.Count(r => r.Status == RpsStatus.Rebuild && !r.IsDeferred);
        public int NewCount => Rows.Count(r => r.Status == RpsStatus.NotDeployed && !r.IsDeferred);
        public int NoMatchCount => Rows.Count(r => r.Status == RpsStatus.NoMatch && !r.IsDeferred);
        public int DeferredCount => Rows.Count(r => r.IsDeferred && !r.DeferralConfigChanged);
        public int ReviewCount => Rows.Count(r => r.DeferralConfigChanged);

        public string CountsSummary
        {
            get
            {
                var parts = new List<string> { $"{TotalCount} circuits" };
                if (StaleCount > 0) parts.Add($"{StaleCount} stale");
                if (RebuildCount > 0) parts.Add($"{RebuildCount} rebuild");
                if (NewCount > 0) parts.Add($"{NewCount} new");
                if (NoMatchCount > 0) parts.Add($"{NoMatchCount} no match");
                if (ReviewCount > 0) parts.Add($"{ReviewCount} review");
                if (DeferredCount > 0) parts.Add($"{DeferredCount} deferred");
                return string.Join(" · ", parts);
            }
        }

        private void LoadRows(IReadOnlyList<RpsCircuitData> circuits)
        {
            foreach (var data in circuits.OrderBy(c => c.CircuitNumber, NaturalStringComparer.OrdinalIgnoreCase))
            {
                var row = new RpsCircuitRowViewModel(data);
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }
        }

        private void OnRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RpsCircuitRowViewModel.IsSelected))
                CommandManager.InvalidateRequerySuggested();
        }

        private bool FilterRow(object item)
        {
            if (item is not RpsCircuitRowViewModel row)
                return false;

            // "Issues" excludes Ok rows and accepted (non-drifted) deferrals; a drifted deferral
            // (REVIEW) is still an open issue and stays visible.
            if (_showOnlyIssues
                && (row.Status == RpsStatus.Ok || (row.IsDeferred && !row.DeferralConfigChanged)))
                return false;

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                // Match on circuit number OR any placed driver's Switch ID. Substring so a base
                // (e.g. "X07") catches its suffixed drivers ("X07a", "X07b").
                string term = _searchText.Trim();
                bool matchesCircuit = row.CircuitNumber != null
                    && row.CircuitNumber.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                bool matchesSwitchId = row.Data.SwitchIds != null
                    && row.Data.SwitchIds.Any(id => id.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!matchesCircuit && !matchesSwitchId)
                    return false;
            }

            return true;
        }

        private void RaiseCountsChanged()
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(StaleCount));
            OnPropertyChanged(nameof(RebuildCount));
            OnPropertyChanged(nameof(NewCount));
            OnPropertyChanged(nameof(NoMatchCount));
            OnPropertyChanged(nameof(DeferredCount));
            OnPropertyChanged(nameof(ReviewCount));
            OnPropertyChanged(nameof(CountsSummary));
        }

        private bool CanToggleDeferral
            => !_isBusy && _selectedRow != null && _selectedRow.Data.CircuitRef.IsValid;

        private void DeferSelected() => ApplyDeferral(true);

        private void ClearDeferral() => ApplyDeferral(false);

        private void ApplyDeferral(bool defer)
        {
            if (!CanToggleDeferral) return;
            var row = _selectedRow;
            var circuitRef = row.Data.CircuitRef;
            // Signature is captured from the config the user is accepting right now.
            string signature = defer ? RpsDeferral.Signature(row.Data) : null;

            IsBusy = true;
            _workQueue.Enqueue(
                () => _ops.SetDeferred(circuitRef, defer, signature),
                result =>
                {
                    try
                    {
                        if (result is bool ok && ok)
                        {
                            row.SetDeferred(defer, signature);
                            RowsView.Refresh();
                            RaiseCountsChanged();
                        }
                        else
                        {
                            System.Windows.MessageBox.Show(
                                "This circuit no longer exists in the project.", "TurboRPS");
                        }
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }

        private void SelectAllStale()
        {
            foreach (var row in Rows.Where(r => r.CanSelect))
                row.IsSelected = true;
            CommandManager.InvalidateRequerySuggested();
        }

        private bool CanReRun() => !_isBusy && Rows.Any(r => r.IsSelected);

        private void ReRunSelected()
        {
            var selected = Rows.Where(r => r.IsSelected && r.Status == RpsStatus.Stale).ToList();
            if (selected.Count == 0) return;

            var swaps = new List<DriverSwap>();
            foreach (var row in selected)
            {
                foreach (var deviceRef in row.Data.DeviceRefs)
                {
                    swaps.Add(new DriverSwap
                    {
                        DeviceRef = deviceRef,
                        NewTypeRef = row.Data.RecommendedTypeRef
                    });
                }
            }

            if (swaps.Count == 0) return;

            IsBusy = true;
            _workQueue.Enqueue(
                () => _ops.SwapDriverTypes(swaps),
                result =>
                {
                    try
                    {
                        foreach (var row in selected)
                            row.RefreshSwapped();

                        RowsView.Refresh();
                        RaiseCountsChanged();

                        int count = result is int n ? n : 0;
                        System.Windows.MessageBox.Show(
                            $"Re-typed {count} power suppl{(count == 1 ? "y" : "ies")} across {selected.Count} circuit(s).",
                            "TurboRPS");
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }

        private void Rescan()
        {
            IsBusy = true;
            _workQueue.Enqueue(
                () => _ops.Rescan(),
                result =>
                {
                    try
                    {
                        if (result is IReadOnlyList<RpsCircuitData> fresh)
                        {
                            foreach (var row in Rows)
                                row.PropertyChanged -= OnRowPropertyChanged;
                            Rows.Clear();
                            LoadRows(fresh);
                            SelectedRow = null;
                            RowsView.Refresh();
                            RaiseCountsChanged();
                        }
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }

        private bool CanSelectInProject()
            => !_isBusy && _selectedRow != null && _selectedRow.Data.CircuitRef.IsValid;

        private void SelectInProject()
        {
            if (!CanSelectInProject()) return;
            var circuitRef = _selectedRow.Data.CircuitRef;

            IsBusy = true;
            _workQueue.Enqueue(
                () => _ops.SelectInProject(circuitRef),
                result =>
                {
                    try
                    {
                        if (result is bool ok && !ok)
                            System.Windows.MessageBox.Show(
                                "This circuit no longer exists in the project.", "TurboRPS");
                    }
                    finally
                    {
                        IsBusy = false;
                    }
                });
        }
    }
}
