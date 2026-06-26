using System;
using System.Collections.Generic;

namespace TurboSuite.Dmx
{
    /// <summary>One decoder load paired with the driver Type sized to feed it (1:1 in practice, §8).</summary>
    public sealed class PoweredDecoder
    {
        public PoweredDecoder(DecoderLoad decoder, DriverType driver)
        {
            Decoder = decoder;
            Driver = driver;
        }

        public DecoderLoad Decoder { get; }
        public DriverType Driver { get; }
    }

    /// <summary>The result of one zone's power pass: the powered decoders plus the coupled cap used.</summary>
    public sealed class PowerPackResult
    {
        public PowerPackResult(IReadOnlyList<PoweredDecoder> decoders, double coupledDecoderCapWatts)
        {
            Decoders = decoders;
            CoupledDecoderCapWatts = coupledDecoderCapWatts;
        }

        public IReadOnlyList<PoweredDecoder> Decoders { get; }

        /// <summary>The ceiling each decoder was packed to: min(decoder C1/C2 cap, largest driver × derate).</summary>
        public double CoupledDecoderCapWatts { get; }

        public int DecoderCount => Decoders.Count;
        public int DriverCount => Decoders.Count;
    }

    /// <summary>
    /// The cross-tier coupling + end-to-end power pass (runs → decoders → drivers) for ONE selected
    /// decoder type. Decision A (§8): the decoder ceiling is clamped to min(decoder cap, largest
    /// driver × derate), so every decoder load fits a driver by construction.
    /// </summary>
    public static class PowerPacker
    {
        /// <summary>The coupled decoder ceiling: min(decoder C1/C2 cap, largest driver × derate).</summary>
        public static double CoupledDecoderCap(DecoderSpec decoder, int channels, double volts,
                                               IReadOnlyList<DriverType> driverCandidates)
        {
            double decoderCap = DecoderPacker.EffectiveWattCap(decoder, channels, volts);
            double largestDriverCap = DriverSelector.LargestEffectiveCap(driverCandidates, volts);
            if (largestDriverCap <= 0)
                throw new InvalidOperationException("No driver Type matches the system voltage; cannot couple the decoder ceiling.");
            return Math.Min(decoderCap, largestDriverCap);
        }

        /// <summary>
        /// Pack one zone's runs into powered decoders using the given (already-selected) decoder type.
        /// Conserves watts; never refuses (over-grouping, §6).
        /// </summary>
        public static PowerPackResult Pack(IReadOnlyList<TapeRun> runs, DecoderSpec decoder, double volts,
                                           IReadOnlyList<DriverType> driverCandidates)
        {
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            if (runs.Count == 0) return new PowerPackResult(Array.Empty<PoweredDecoder>(), 0);

            int channels = DecoderPacker.SingleChannelsOf(runs);
            double coupledCap = CoupledDecoderCap(decoder, channels, volts, driverCandidates);

            var decoderLoads = DecoderPacker.PackToCap(runs, coupledCap);

            var powered = new List<PoweredDecoder>(decoderLoads.Count);
            foreach (var load in decoderLoads)
            {
                var driver = DriverSelector.SelectSmallestFitting(driverCandidates, load.TotalWatts, volts);
                if (driver is null)
                    throw new InvalidOperationException(
                        $"Decoder load {load.TotalWatts:F1} W found no driver despite the coupled cap — coupling invariant violated.");
                powered.Add(new PoweredDecoder(load, driver.Value));
            }

            return new PowerPackResult(powered, coupledCap);
        }
    }
}
