#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;

namespace TurboSuite.Number.ViewModels
{
    public abstract class TabViewModelBase : ViewModelBase
    {
        protected bool _isUpdating;
        private bool _isCascadeEnabled = false;

        public ObservableCollection<NumberableRowViewModel> Rows { get; } = new ObservableCollection<NumberableRowViewModel>();

        public string TabHeader { get; }

        public int RowCount => Rows.Count;

        public bool IsCascadeEnabled
        {
            get => _isCascadeEnabled;
            set => SetProperty(ref _isCascadeEnabled, value);
        }

        public ICommand AutoNumberCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand ToggleCascadeCommand { get; }

        protected TabViewModelBase(string tabHeader)
        {
            TabHeader = tabHeader;
            AutoNumberCommand = new RelayCommand(DoAutoNumber);
            ApplyCommand = new RelayCommand(Apply);
            ToggleCascadeCommand = new RelayCommand(() => IsCascadeEnabled = !IsCascadeEnabled);
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
            if (!int.TryParse(changedRow.Value, out int startValue))
                return;

            if (!IsCascadeEnabled) return;

            var sorted = GetSortedRows();
            int index = sorted.IndexOf(changedRow);
            if (index < 0) return;

            _isUpdating = true;
            try
            {
                for (int i = index + 1; i < sorted.Count; i++)
                {
                    startValue++;
                    sorted[i].Value = FormatNumber(startValue);
                }
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
