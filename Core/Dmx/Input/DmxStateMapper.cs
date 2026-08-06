#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Persistence;

namespace TurboSuite.Dmx.Input
{
    /// <summary>
    /// The persisted <see cref="DmxModuleState"/> → engine-input mapping, in one place.
    ///
    /// Two callers need it and they are not the same caller: the TurboDMX window maps state once at
    /// open and then works from live UI edits, while <see cref="DmxHeadlessSolve"/> maps it on every
    /// call because the persisted design IS its input. Sharing the mapping is what keeps a BOM built
    /// outside the window from disagreeing with the window about the same saved job.
    /// </summary>
    public static class DmxStateMapper
    {
        /// <summary>The saved profile, falling back to Lutron for an unknown or missing name.</summary>
        public static DmxProfile ToProfile(DmxSettingsDto? dto) => DmxProfile.ByName(dto?.Profile);

        /// <summary>The saved Kind-2 job policy. A never-saved DTO round-trips to the same defaults the
        /// window opens with, so applying it is a no-op rather than a zeroing.</summary>
        public static DmxJobSettings ToJobSettings(DmxSettingsDto? dto)
        {
            var s = dto ?? new DmxSettingsDto();
            return new DmxJobSettings
            {
                SystemVolts = s.SystemVolts,
                BreakerAmps = s.BreakerAmps,
                FeedVolts = s.FeedVolts,
                BreakerContinuousDerate = s.BreakerContinuousDerate,
                MaxDriversPerBreaker = s.MaxDriversPerBreaker,
                PullUpSizes = s.PullUpSizes,
                BreakerBasis = Enum.TryParse<BreakerBasis>(s.BreakerBasis, out var basis)
                    ? basis : BreakerBasis.DriverRating,
            };
        }

        /// <summary>
        /// The saved loops as engine declarations, reconciled against the zones that actually exist now.
        ///
        /// Mirrors the window's rebuild rules exactly, and for the same reasons: a zone that has since
        /// been renamed or deleted drops out rather than failing the solve, a zone named by two loops
        /// sticks to the first (single membership), and a loop left with no zones is skipped — an empty
        /// declaration would claim an interface for nothing.
        /// </summary>
        public static List<LoopDeclaration> ToLoopDeclarations(
            IEnumerable<DmxLoopDto>? loops, IEnumerable<string>? existingZoneNames)
        {
            var declarations = new List<LoopDeclaration>();
            if (loops == null) return declarations;

            var zoneSet = new HashSet<string>(existingZoneNames ?? Enumerable.Empty<string>(),
                                              StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dto in loops.OrderBy(l => l.Order))
            {
                var zones = new List<string>();
                foreach (string zone in dto.ZoneValues ?? new List<string>())
                {
                    if (!zoneSet.Contains(zone) || !used.Add(zone)) continue;
                    zones.Add(zone);
                }
                if (zones.Count == 0) continue;

                declarations.Add(new LoopDeclaration(dto.Name, zones, dto.ReservedChannels));
            }
            return declarations;
        }

        /// <summary>
        /// The curated decoder kit: the discovered candidates the designer ticked, by stable type id.
        ///
        /// An empty saved list means <b>never curated</b>, not "none" — but it still yields nothing
        /// here, because a job whose kit was never picked has no parts to solve with. The caller
        /// reports that as a diagnostic rather than inventing a default kit.
        /// </summary>
        public static List<DmxDecoderCandidate> ToCuratedDecoders(
            IEnumerable<DmxDecoderCandidate>? discovered, DmxSettingsDto? dto)
        {
            var ids = new HashSet<string>(dto?.DecoderTypeIds ?? new List<string>());
            return (discovered ?? Enumerable.Empty<DmxDecoderCandidate>())
                .Where(c => ids.Contains(c.TypeId)).ToList();
        }

        /// <summary>The curated driver kit — same gesture and same storage as the decoders.</summary>
        public static List<DmxDriverCandidate> ToCuratedDrivers(
            IEnumerable<DmxDriverCandidate>? discovered, DmxSettingsDto? dto)
        {
            var ids = new HashSet<string>(dto?.DriverTypeIds ?? new List<string>());
            return (discovered ?? Enumerable.Empty<DmxDriverCandidate>())
                .Where(c => ids.Contains(c.TypeId)).ToList();
        }
    }
}
