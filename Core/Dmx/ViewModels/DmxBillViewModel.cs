#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>One "type × count" line in the decoder/driver BOM readout.</summary>
    public sealed class DmxBomLineViewModel
    {
        public DmxBomLineViewModel(string type, int count) { Type = type; Count = count; }
        public string Type { get; }
        public int Count { get; }
        public string Display => $"{Type} × {Count}";
    }

    /// <summary>
    /// The always-on Bill dashboard (TurboDMX-UI-Structure §2 "the dashboard IS the bill"). Either an
    /// empty/error verdict state (the static factories) or the deterministic counts of a solved
    /// <see cref="DmxBill"/>. Recomputed on every Run; never mutates the model.
    /// </summary>
    public sealed class DmxBillViewModel : ViewModelBase
    {
        private DmxBillViewModel(bool hasResult, bool isError, string status)
        {
            HasResult = hasResult;
            IsError = isError;
            StatusMessage = status;
        }

        public bool HasResult { get; private set; }

        /// <summary>True ⇒ the verdict is a pre-solve gate failure (red), not a normal result.</summary>
        public bool IsError { get; }

        /// <summary>The verdict line — "OK", a gate refusal, or guidance ("Tick a decoder type").</summary>
        public string StatusMessage { get; }

        public int InterfaceCount { get; private set; }
        public int Decoders { get; private set; }
        public int Drivers { get; private set; }
        public int Breakers { get; private set; }
        public int Processors { get; private set; }
        public int Links { get; private set; }
        public int Repeaters { get; private set; }
        public int ChannelsUsed { get; private set; }
        public string ChannelsText { get; private set; } = "—";
        public string TotalWattsText { get; private set; } = "—";

        public ObservableCollection<DmxBomLineViewModel> DecoderBom { get; } = new ObservableCollection<DmxBomLineViewModel>();
        public ObservableCollection<DmxBomLineViewModel> DriverBom { get; } = new ObservableCollection<DmxBomLineViewModel>();

        /// <summary>Lock-aware REVIEW verdicts (§8c) — locked-zone changes that would mislabel an issued
        /// DEC #. Empty unless Locked and a change collides; shown as an amber list, never a popup.</summary>
        public ObservableCollection<string> Reviews { get; } = new ObservableCollection<string>();
        public bool NeedsReview => Reviews.Count > 0;

        /// <summary>The standing empty state before the first Run / when there's nothing to solve.</summary>
        public static DmxBillViewModel Empty(string guidance) => new DmxBillViewModel(false, false, guidance);

        /// <summary>A pre-solve gate refusal (UnmappableTape / OverCapRuns / OverCapLoops / bad loop).</summary>
        public static DmxBillViewModel Error(string message) => new DmxBillViewModel(false, true, message);

        /// <summary>A successful solve — the deterministic bill (TurboDMX-UI-Structure §2). Lock-aware REVIEW
        /// verdicts (§8c) ride along when supplied.</summary>
        public static DmxBillViewModel FromBill(DmxBill bill, int channelCeiling, IEnumerable<string>? reviews = null)
        {
            var vm = new DmxBillViewModel(true, false, "OK")
            {
                InterfaceCount = bill.InterfaceCount,
                Decoders = bill.TotalDecoders,
                Drivers = bill.TotalDrivers,
                Breakers = bill.RequiredBreakers,
                Processors = bill.RequiredProcessors,
                Links = bill.RequiredLinks,
                Repeaters = bill.TotalRepeaters,
                ChannelsUsed = bill.TotalChannels,
            };

            // Total used vs the budget the interfaces provide (count × per-interface ceiling).
            int capacity = bill.InterfaceCount * channelCeiling;
            vm.ChannelsText = string.Format(CultureInfo.InvariantCulture, "{0} / {1}", bill.TotalChannels, capacity);
            vm.TotalWattsText = string.Format(CultureInfo.InvariantCulture, "{0:0} W", bill.TotalWatts);

            foreach (var kv in bill.DecodersByType.OrderBy(k => k.Key))
                vm.DecoderBom.Add(new DmxBomLineViewModel(kv.Key, kv.Value));
            foreach (var kv in bill.DriversByType.OrderBy(k => k.Key))
                vm.DriverBom.Add(new DmxBomLineViewModel(kv.Key, kv.Value));

            if (reviews != null)
                foreach (var r in reviews) vm.Reviews.Add(r);

            return vm;
        }
    }
}
