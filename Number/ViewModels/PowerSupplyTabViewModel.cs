#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Number.Models;
using TurboSuite.Number.Services;

namespace TurboSuite.Number.ViewModels
{
    public class PowerSupplyTabViewModel : TabViewModelBase
    {
        private readonly Document _doc;
        private readonly ExternalEvent _externalEvent;
        private readonly RevitApiRequestHandler _handler;
        private string _prefix = "X";
        private string _suffix = "";

        public string Prefix
        {
            get => _prefix;
            set
            {
                if (SetProperty(ref _prefix, value ?? ""))
                    RaiseRequest(new SavePrefixSuffixRequest { Prefix = _prefix, Suffix = _suffix });
            }
        }

        public string Suffix
        {
            get => _suffix;
            set
            {
                if (SetProperty(ref _suffix, value ?? ""))
                    RaiseRequest(new SavePrefixSuffixRequest { Prefix = _prefix, Suffix = _suffix });
            }
        }

        public PowerSupplyTabViewModel(Document doc, List<DeviceNumberRow> powerSupplies,
            ExternalEvent externalEvent, RevitApiRequestHandler handler)
            : base("Power Supplies", externalEvent, handler)
        {
            _doc = doc;
            _externalEvent = externalEvent;
            _handler = handler;

            foreach (var d in powerSupplies)
            {
                AddRow(new NumberableRowViewModel(
                    d.ElementId,
                    d.Model,
                    d.SwitchId,
                    circuitNumber: d.CircuitNumber,
                    circuitElementId: d.CircuitElementId,
                    loadName: d.LoadName,
                    typeName: d.TypeName,
                    mark: d.Mark));
            }

            var (savedPrefix, savedSuffix) = RoomOrderStorageService.LoadPrefixSuffix(doc);
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
                if (circuitId != ElementId.InvalidElementId)
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

                if (group.Count == 1)
                {
                    group[0].Value = $"{_prefix}{padded}{_suffix}";
                }
                else
                {
                    for (int g = 0; g < group.Count; g++)
                    {
                        char letter = (char)('a' + g);
                        group[g].Value = $"{_prefix}{padded}{letter}{_suffix}";
                    }
                }

                i += group.Count;
            }
        }

        protected override bool TryParseNumber(string input, out int value)
        {
            // Strip known prefix/suffix so "X40" parses as 40
            if (!string.IsNullOrEmpty(_prefix) && input.StartsWith(_prefix, System.StringComparison.OrdinalIgnoreCase))
                input = input.Substring(_prefix.Length);
            if (!string.IsNullOrEmpty(_suffix) && input.EndsWith(_suffix, System.StringComparison.OrdinalIgnoreCase))
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
            RaiseRequest(new WriteDeviceSwitchIdsRequest { Rows = Rows });
        }

        private void RaiseRequest(RevitApiRequest request)
        {
            _handler.CurrentRequest = request;
            _externalEvent.Raise();
        }
    }
}
