#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Number.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public abstract class TabViewModelBase : ViewModelBase
    {
        protected readonly IRevitWorkQueue _workQueue;
        private readonly IDeviceSelector _selector;
        protected bool _isUpdating;
        private bool _isCascadeEnabled = false;
        private NumberableRowViewModel _selectedRow;

        public ObservableCollection<NumberableRowViewModel> Rows { get; } = new ObservableCollection<NumberableRowViewModel>();

        public string TabHeader { get; }

        public int RowCount => Rows.Count;

        public bool IsCascadeEnabled
        {
            get => _isCascadeEnabled;
            set => SetProperty(ref _isCascadeEnabled, value);
        }

        /// <summary>
        /// Bound to the tab grid's <c>SelectedItem</c>; the target of
        /// <see cref="SelectInProjectCommand"/>.
        /// </summary>
        public NumberableRowViewModel SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (SetProperty(ref _selectedRow, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        public ICommand AutoNumberCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ToggleCascadeCommand { get; }
        public ICommand SelectInProjectCommand { get; }

        protected TabViewModelBase(string tabHeader, IRevitWorkQueue workQueue, IDeviceSelector selector)
        {
            TabHeader = tabHeader;
            _workQueue = workQueue;
            _selector = selector;
            AutoNumberCommand = new RelayCommand(DoAutoNumber);
            ApplyCommand = new RelayCommand(Apply);
            ToggleCascadeCommand = new RelayCommand(() => IsCascadeEnabled = !IsCascadeEnabled);
            SelectInProjectCommand = new RelayCommand(SelectInProject, CanSelectInProject);
        }

        private bool CanSelectInProject() => _selectedRow != null && _selectedRow.ElementId.IsValid;

        private void SelectInProject()
        {
            if (!CanSelectInProject()) return;
            var elementRef = _selectedRow.ElementId;

            _workQueue.Enqueue(
                () => _selector.SelectInProject(elementRef),
                result =>
                {
                    if (result is bool ok && !ok)
                        System.Windows.MessageBox.Show(
                            "This device no longer exists in the project.", "TurboNumber");
                });
        }

        protected void AddRow(NumberableRowViewModel row)
        {
            row.ValueChanged += OnRowValueChanged;
            Rows.Add(row);
        }

        protected List<NumberableRowViewModel> GetSortedRows()
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(Rows);
            return view.Cast<NumberableRowViewModel>().ToList();
        }

        private void OnRowValueChanged(object sender, EventArgs e)
        {
            if (_isUpdating) return;

            var changedRow = (NumberableRowViewModel)sender;
            if (!TryParseNumber(changedRow.Value, out int startValue))
                return;

            if (!IsCascadeEnabled) return;

            var sorted = GetSortedRows();
            int index = sorted.IndexOf(changedRow);
            if (index < 0) return;

            _isUpdating = true;
            try
            {
                CascadeFrom(sorted, index, startValue);
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void DoAutoNumber()
        {
            _isUpdating = true;
            try
            {
                AutoNumber();
            }
            finally
            {
                _isUpdating = false;
            }
        }

        protected virtual void AutoNumber()
        {
            var sorted = GetSortedRows();
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].Value = FormatNumber(i + 1);
            }
        }

        protected virtual void CascadeFrom(List<NumberableRowViewModel> sorted, int index, int startValue)
        {
            sorted[index].Value = FormatNumber(startValue);
            for (int i = index + 1; i < sorted.Count; i++)
            {
                startValue++;
                sorted[i].Value = FormatNumber(startValue);
            }
        }

        protected virtual bool TryParseNumber(string input, out int value)
        {
            return int.TryParse(input, out value);
        }

        protected virtual string FormatNumber(int value)
        {
            return value < 10 ? $"0{value}" : value.ToString();
        }

        protected void ApplyDefaultSort()
        {
            var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
            view.CustomSort = new ValueThenMarkComparer();
        }

        protected abstract void Apply();

        protected class ValueThenMarkComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                var a = (NumberableRowViewModel)x;
                var b = (NumberableRowViewModel)y;
                string keyA = string.IsNullOrEmpty(a.Value) ? a.Mark : a.Value;
                string keyB = string.IsNullOrEmpty(b.Value) ? b.Mark : b.Value;
                return string.Compare(keyA, keyB, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
