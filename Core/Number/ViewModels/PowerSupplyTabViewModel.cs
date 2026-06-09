#nullable disable
using System;
using System.Collections.Generic;
using TurboSuite.Abstractions;
using TurboSuite.Number.Services;

namespace TurboSuite.Number.ViewModels
{
    public class PowerSupplyTabViewModel : TabViewModelBase
    {
        private readonly IRevitWorkQueue _workQueue;
        private readonly ISwitchIdWriter _switchIdWriter;
        private readonly IPrefixSuffixStore _prefixSuffixStore;
        private string _prefix = "X";
        private string _suffix = "";

        public string Prefix
        {
            get => _prefix;
            set
            {
                if (SetProperty(ref _prefix, value ?? ""))
                    SavePrefixSuffix();
            }
        }

        public string Suffix
        {
            get => _suffix;
            set
            {
                if (SetProperty(ref _suffix, value ?? ""))
                    SavePrefixSuffix();
            }
        }

        public PowerSupplyTabViewModel(IReadOnlyList<NumberableRowViewModel> rows,
            string savedPrefix, string savedSuffix,
            IRevitWorkQueue workQueue, ISwitchIdWriter switchIdWriter,
            IPrefixSuffixStore prefixSuffixStore)
            : base("Power Supplies")
        {
            _workQueue = workQueue;
            _switchIdWriter = switchIdWriter;
            _prefixSuffixStore = prefixSuffixStore;

            foreach (var row in rows)
                AddRow(row);

            // Prefix/suffix are read from ExtensibleStorage at collection time and
            // passed in — a Core ctor cannot read Revit synchronously.
            if (savedPrefix != null) _prefix = savedPrefix;
            if (savedSuffix != null) _suffix = savedSuffix;

            ApplyDefaultSort();
        }

        protected override void AutoNumber()
        {
            var sorted = GetSortedRows();
            int baseNumber = 0;
            int i = 0;

            while (i < sorted.Count)
            {
                baseNumber++;
                var circuitId = sorted[i].CircuitElementId;

                var group = new List<NumberableRowViewModel> { sorted[i] };
                if (circuitId.IsValid)
                {
                    for (int j = i + 1; j < sorted.Count; j++)
                    {
                        if (sorted[j].CircuitElementId == circuitId)
                            group.Add(sorted[j]);
                        else
                            break;
                    }
                }

                string padded = PadNumber(baseNumber);
                AssignGroupValues(group, padded);

                i += group.Count;
            }
        }

        /// <summary>
        /// Writes the Switch ID for one circuit's power supplies. A single supply gets
        /// the bare number; multiples get a/b/c by descending model Y so the suffix
        /// tracks plan top-to-bottom (TurboDriver stacks 'a' at the highest Y), not the
        /// grid/Mark sort order the user happens to be viewing.
        /// </summary>
        private void AssignGroupValues(List<NumberableRowViewModel> group, string padded)
        {
            if (group.Count == 1)
            {
                group[0].Value = $"{_prefix}{padded}{_suffix}";
                return;
            }

            group.Sort((a, b) => b.PositionY.CompareTo(a.PositionY));
            for (int g = 0; g < group.Count; g++)
            {
                char letter = (char)('a' + g);
                group[g].Value = $"{_prefix}{padded}{letter}{_suffix}";
            }
        }

        protected override void CascadeFrom(List<NumberableRowViewModel> sorted, int index, int startValue)
        {
            int baseNumber = startValue;
            int i = index;

            while (i < sorted.Count)
            {
                var circuitId = sorted[i].CircuitElementId;

                // Gather consecutive rows on the same circuit
                var group = new List<NumberableRowViewModel> { sorted[i] };
                if (circuitId.IsValid)
                {
                    for (int j = i + 1; j < sorted.Count; j++)
                    {
                        if (sorted[j].CircuitElementId == circuitId)
                            group.Add(sorted[j]);
                        else
                            break;
                    }
                }

                string padded = PadNumber(baseNumber);
                AssignGroupValues(group, padded);

                i += group.Count;
                baseNumber++;
            }
        }

        protected override bool TryParseNumber(string input, out int value)
        {
            // Strip known prefix/suffix so "X40" parses as 40
            if (!string.IsNullOrEmpty(_prefix) && input.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                input = input.Substring(_prefix.Length);
            if (!string.IsNullOrEmpty(_suffix) && input.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase))
                input = input.Substring(0, input.Length - _suffix.Length);
            return int.TryParse(input, out value);
        }

        protected override string FormatNumber(int value)
        {
            return $"{_prefix}{PadNumber(value)}{_suffix}";
        }

        private static string PadNumber(int value)
        {
            return value < 10 ? $"0{value}" : value.ToString();
        }

        protected override void Apply()
        {
            _workQueue.Enqueue(() => { _switchIdWriter.WriteSwitchIds(Rows); return null; }, null);
        }

        private void SavePrefixSuffix()
        {
            var prefix = _prefix;
            var suffix = _suffix;
            _workQueue.Enqueue(() => { _prefixSuffixStore.Save(prefix, suffix); return null; }, null);
        }
    }
}
