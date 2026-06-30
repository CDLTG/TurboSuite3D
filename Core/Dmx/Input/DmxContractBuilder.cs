#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Input;

namespace TurboSuite.Dmx
{
    /// <summary>The editable Kind-2 job-policy knobs the window's Settings panel exposes.
    /// Profile-seeded, overridable. Separate from <see cref="DmxProfile"/> (which supplies ceiling + link
    /// caps) so the panel can override policy without touching the profile.</summary>
    public sealed class DmxJobSettings
    {
        public double SystemVolts { get; set; } = 24.0;
        public double BreakerAmps { get; set; } = 20.0;
        public double FeedVolts { get; set; } = 120.0;
        public double BreakerContinuousDerate { get; set; } = 0.8;
        public int MaxDriversPerBreaker { get; set; } = 0;     // 0 = no inrush count cap
        public int MaxDevicesPerSegment { get; set; } = 32;    // D4
        public int ReservedChannels { get; set; } = 0;
        public BreakerBasis BreakerBasis { get; set; } = BreakerBasis.ConnectedLoad;
    }

    /// <summary>
    /// Assembles a <see cref="DmxContract"/> from the window's declarations: the selected
    /// <see cref="DmxProfile"/> (ceiling + link caps), the <see cref="DmxJobSettings"/> (Kind-2 policy),
    /// and the curated decoder/driver candidate pools (Q10). Pure / Revit-free — the same contract the
    /// engine harness builds from a scenario file, just sourced from the UI instead of text.
    /// </summary>
    public static class DmxContractBuilder
    {
        public static DmxContract Build(
            DmxProfile profile,
            DmxJobSettings settings,
            IReadOnlyList<DmxDecoderCandidate> decoders,
            IReadOnlyList<DmxDriverCandidate> drivers)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (decoders == null) throw new ArgumentNullException(nameof(decoders));
            if (drivers == null) throw new ArgumentNullException(nameof(drivers));

            var decoderPool = decoders
                .Select(d => new DecoderSpec(d.Name, d.MaxOutputs, d.MaxAmpsPerOutput, d.MaxWatts))
                .ToList();

            var driverPool = drivers
                .Select(d => new DriverType(d.Name, d.RatedWatts, d.OperatingVolts, d.DeratingFactorRaw))
                .ToList();

            return new DmxContract(
                decoderPool, driverPool,
                systemVolts: settings.SystemVolts,
                channelCeiling: profile.ChannelCeiling,
                reservedChannels: settings.ReservedChannels,
                maxDevicesPerSegment: settings.MaxDevicesPerSegment,
                breakerAmps: settings.BreakerAmps,
                feedVolts: settings.FeedVolts,
                breakerContinuousDerate: settings.BreakerContinuousDerate,
                maxDriversPerBreaker: settings.MaxDriversPerBreaker,
                breakerBasis: settings.BreakerBasis,
                linkChannelCapacity: profile.LinkChannelCapacity,
                linkDeviceCapacity: profile.LinkDeviceCapacity,
                linksPerProcessor: profile.LinksPerProcessor);
        }
    }
}
