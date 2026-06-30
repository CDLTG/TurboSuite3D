#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Shim-side builder that turns the live document into Revit-free
    /// <see cref="RpsCircuitData"/> DTOs for the TurboRPS dashboard: collect circuits +
    /// driver candidates, run the recommendation, summarize the placed supplies, and
    /// classify staleness. Used both at command startup and on Rescan so the two paths
    /// can never diverge.
    /// </summary>
    public static class RpsCircuitDataBuilder
    {
        public static IReadOnlyList<RpsCircuitData> Build(Document doc)
        {
            var circuitService = new CircuitCollectorService();
            var typeService = new FamilyTypeCollectorService();

            var circuits = circuitService.GetFilteredCircuits(doc);
            var allTypes = typeService.GetAllLightingDeviceTypes(doc);
            var candidates = typeService.GetDriverCandidates(allTypes);

            // Only valid driver types count as "placed supplies" — keypads/sensors that share
            // the OST_LightingDevices category and the circuit are ignored.
            var validBySymbol = candidates
                .Where(c => c.IsValidDriver)
                .GroupBy(c => c.SymbolRef)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new List<RpsCircuitData>();

            foreach (var circuit in circuits)
            {
                // Size the driver over fixtures that actually need one. On a switched/relay
                // circuit, line-voltage fixtures (e.g. recessed downlights) ride the same
                // circuit for total-wattage/power-density but must not inflate the driver
                // recommendation — and excluding them keeps the recommendation matching what
                // TurboDriver placed (it sizes off the user's RPS-fixture selection).
                var rpsFixtures = circuit.LightingFixtures
                    .Where(f => f.HasRemotePowerSupply)
                    .ToList();

                var recommendation = new DriverSelectionService()
                    .GetRecommendation(rpsFixtures, candidates);

                // Summarize placed driver instances.
                var placedDeviceRefs = new List<ElementRef>();
                var placedTypeRefs = new List<ElementRef>();
                var switchIds = new List<string>();
                foreach (var typeList in circuit.DevicesByType.Values)
                {
                    foreach (var device in typeList)
                    {
                        var typeRef = device.CurrentFamilyTypeId.ToRef();
                        if (!validBySymbol.ContainsKey(typeRef))
                            continue;
                        placedDeviceRefs.Add(device.DeviceId.ToRef());
                        placedTypeRefs.Add(typeRef);
                        if (!string.IsNullOrWhiteSpace(device.SwitchID))
                            switchIds.Add(device.SwitchID.Trim());
                    }
                }
                switchIds.Sort(NaturalStringComparer.OrdinalIgnoreCase);

                int placedCount = placedDeviceRefs.Count;
                var distinctTypeRefs = placedTypeRefs.Distinct().ToList();
                int distinctCount = distinctTypeRefs.Count;
                DriverCandidateInfo placedCandidate = distinctCount == 1
                    ? validBySymbol[distinctTypeRefs[0]]
                    : null;

                // DMX-decoder discriminator (TurboRPS-2): a wired decoder device (OST_LightingDevices
                // with DMX Channels > 0) means the circuit is powered by the decoder, not a wattage-sized
                // driver TurboRPS models. Decoder presence is authoritative — DMX sizing/packing belongs
                // to TurboDMX, so a decoder-controlled circuit is never given a driver recommendation here
                // and is flagged green ("present & wired"), regardless of fixture-param hygiene. (Decoders
                // are also excluded from the driver candidate pool in FamilyTypeCollectorService, so they
                // no longer inflate the placed count or feed a bogus repack.)
                var decoders = circuit.DevicesByType.Values
                    .SelectMany(list => list)
                    .Where(d => d.IsDecoder)
                    .ToList();
                int decoderCount = decoders.Count;
                string decoderTypeName = decoders
                    .Select(d => d.CurrentFamilyTypeName)
                    .Distinct()
                    .Count() == 1 ? decoders[0].CurrentFamilyTypeName : string.Empty;

                bool isDmxDecoderManaged = decoderCount > 0;

                var classification = StaleClassifier.Classify(
                    placedCount, distinctCount, placedCandidate, recommendation, isDmxDecoderManaged);

                bool hasSplit = recommendation?.SubDriverAssignments != null
                    && recommendation.SubDriverAssignments
                        .SelectMany(a => a.Segments ?? new List<FixtureSegment>())
                        .Any(s => s.IsSplit);

                string dimming = string.Join(" / ",
                    rpsFixtures
                        .Where(f => !string.IsNullOrWhiteSpace(f.DimmingProtocol))
                        .Select(f => f.DimmingProtocol)
                        .Distinct(StringComparer.OrdinalIgnoreCase));

                var reco = recommendation?.RecommendedCandidate;

                // Deferral flag persisted on the circuit element (shared via the model).
                var (deferred, deferredSig) = RpsDeferralStorageService.Read(doc.GetElement(circuit.CircuitId));

                result.Add(new RpsCircuitData
                {
                    CircuitRef = circuit.CircuitId.ToRef(),
                    CircuitNumber = circuit.CircuitNumber,
                    LoadName = circuit.LoadName,
                    DimmingProtocol = dimming,
                    ApparentPower = circuit.ApparentPower,
                    RpsLoadWatts = rpsFixtures.Sum(f => f.EffectiveWattage),
                    Panel = circuit.Panel,

                    DeviceRefs = placedDeviceRefs,
                    PlacedTypeName = placedCandidate?.FamilyTypeName ?? string.Empty,
                    SwitchIds = switchIds,
                    PlacedCount = placedCount,
                    DistinctPlacedTypeCount = distinctCount,
                    PlacedChannels = placedCandidate?.SubDriverCount ?? 0,
                    DecoderCount = decoderCount,
                    DecoderTypeName = decoderTypeName,

                    Recommendation = recommendation,
                    Status = classification.Status,
                    RebuildReason = classification.RebuildReason,
                    HasSplitSegments = hasSplit,

                    RecommendedTypeRef = reco?.SymbolRef ?? ElementRef.None,
                    RecommendedTypeName = reco?.FamilyTypeName ?? string.Empty,
                    RecommendedCount = recommendation?.DriverCount ?? 0,

                    Fixtures = rpsFixtures,

                    IsDeferred = deferred,
                    DeferredSignature = deferredSig
                });
            }

            return result;
        }
    }
}
