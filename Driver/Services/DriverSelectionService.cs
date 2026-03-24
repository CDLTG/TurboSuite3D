#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Implements the driver selection algorithm using real project driver types
    /// </summary>
    public class DriverSelectionService
    {
        private const double DefaultFallbackSubDriverWattage = 96.0;

        /// <summary>
        /// Get the recommended driver configuration for a list of fixtures,
        /// evaluating each valid driver candidate from the project
        /// </summary>
        public DriverRecommendation GetRecommendation(List<FixtureData> fixtures, List<DriverCandidateInfo> driverCandidates)
        {
            if (fixtures == null || fixtures.Count == 0)
                return null;

            var fixturesWithPower = fixtures.Where(f => f.EffectiveWattage > 0).ToList();
            if (fixturesWithPower.Count == 0)
                return null;

            var fixtureManufacturers = new HashSet<string>(
                fixturesWithPower
                    .Where(f => !string.IsNullOrWhiteSpace(f.Manufacturer))
                    .Select(f => f.Manufacturer),
                StringComparer.OrdinalIgnoreCase);

            var fixtureDimmingProtocols = new HashSet<string>(
                fixturesWithPower
                    .Where(f => !string.IsNullOrWhiteSpace(f.DimmingProtocol))
                    .Select(f => f.DimmingProtocol),
                StringComparer.OrdinalIgnoreCase);

            var fixtureVoltages = new HashSet<string>(
                fixturesWithPower
                    .Where(f => !string.IsNullOrWhiteSpace(f.Voltage))
                    .Select(f => f.Voltage),
                StringComparer.OrdinalIgnoreCase);

            var validCandidates = driverCandidates?.Where(c => c.IsValidDriver).ToList()
                ?? new List<DriverCandidateInfo>();

            if (validCandidates.Count == 0)
            {
                // No valid candidates — compute required specs using fallback for reporting
                var fallbackSegments = new List<FixtureSegment>();
                foreach (var fixture in fixturesWithPower)
                {
                    fallbackSegments.AddRange(SplitFixture(fixture, DefaultFallbackSubDriverWattage));
                }
                var fallbackSubs = PackSegments(fallbackSegments, DefaultFallbackSubDriverWattage);
                int requiredSubs = fallbackSubs.Count;

                return new DriverRecommendation
                {
                    DriverCount = 0,
                    DriverType = null,
                    SubDriversPerDriver = 0,
                    TotalSubDrivers = requiredSubs,
                    DisplayText = "No suitable driver found",
                    SubDriverAssignments = fallbackSubs,
                    RecommendedCandidate = null,
                    HasMatch = false,
                    WarningMessage = $"No suitable driver found. Required: {requiredSubs} sub-driver(s), {DefaultFallbackSubDriverWattage:F0}W each"
                };
            }

            // Evaluate each valid candidate
            CandidateEvaluation bestEval = null;

            foreach (var candidate in validCandidates)
            {
                var eval = EvaluateCandidate(candidate, fixturesWithPower, fixtureManufacturers, fixtureDimmingProtocols, fixtureVoltages);
                if (eval == null)
                    continue;

                if (bestEval == null || IsBetterCandidate(eval, bestEval))
                {
                    bestEval = eval;
                }
            }

            if (bestEval == null)
            {
                return new DriverRecommendation
                {
                    DriverCount = 0,
                    DriverType = null,
                    SubDriversPerDriver = 0,
                    TotalSubDrivers = 0,
                    DisplayText = "No suitable driver found",
                    SubDriverAssignments = new List<SubDriverAssignment>(),
                    RecommendedCandidate = null,
                    HasMatch = false,
                    WarningMessage = "No suitable driver configuration found for this circuit's fixtures"
                };
            }

            var best = bestEval;

            // Assign driver indices to sub-drivers (unless already assigned by fixture-limited packing)
            if (!best.DriverIndicesPreAssigned)
            {
                for (int i = 0; i < best.SubDrivers.Count; i++)
                {
                    best.SubDrivers[i].DriverIndex = (i / best.Candidate.SubDriverCount) + 1;
                    best.SubDrivers[i].Capacity = best.Candidate.SubDriverPower;
                }
            }

            string catalogNumber = best.Candidate.FamilySymbol?.LookupParameter("Catalog Number1")?.AsString() ?? "";
            string manufacturer = best.Candidate.Manufacturer ?? "";
            string displayBase = $"{catalogNumber} | {manufacturer}";
            string displayText = best.DriversNeeded > 1
                ? $"(x{best.DriversNeeded}) {displayBase}"
                : displayBase;

            return new DriverRecommendation
            {
                DriverCount = best.DriversNeeded,
                DriverType = best.Candidate.FamilyTypeName,
                SubDriversPerDriver = best.Candidate.SubDriverCount,
                TotalSubDrivers = best.TotalSubsProvided,
                DisplayText = displayText,
                SubDriverAssignments = best.SubDrivers,
                RecommendedCandidate = best.Candidate,
                HasMatch = true,
                WarningMessage = null
            };
        }

        private CandidateEvaluation EvaluateCandidate(
            DriverCandidateInfo candidate,
            List<FixtureData> fixturesWithPower,
            HashSet<string> fixtureManufacturers,
            HashSet<string> fixtureDimmingProtocols,
            HashSet<string> fixtureVoltages)
        {
            // Hard filter: if fixtures have dimming protocol(s) set, candidate must match
            if (fixtureDimmingProtocols.Count > 0
                && !string.IsNullOrWhiteSpace(candidate.DimmingProtocol)
                && !fixtureDimmingProtocols.Contains(candidate.DimmingProtocol))
            {
                return null;
            }

            // Hard filter: if fixtures have voltage(s) set, candidate must match
            if (fixtureVoltages.Count > 0
                && !string.IsNullOrWhiteSpace(candidate.Voltage)
                && !fixtureVoltages.Contains(candidate.Voltage))
            {
                return null;
            }

            double maxSubWattage = candidate.SubDriverPower;

            // Split fixtures using this candidate's sub-driver capacity
            var segments = new List<FixtureSegment>();
            foreach (var fixture in fixturesWithPower)
            {
                segments.AddRange(SplitFixture(fixture, maxSubWattage));
            }

            List<SubDriverAssignment> subDrivers;
            int driversNeeded;
            bool driverIndicesPreAssigned = false;

            if (candidate.MaximumFixtures > 0)
            {
                // Pack with fixture limit: each driver serves at most MaximumFixtures fixtures
                subDrivers = PackSegmentsWithFixtureLimit(
                    segments, maxSubWattage, candidate.MaximumFixtures,
                    candidate.SubDriverCount, out driversNeeded);
                driverIndicesPreAssigned = true;
            }
            else
            {
                subDrivers = PackSegments(segments, maxSubWattage);
                driversNeeded = (int)Math.Ceiling((double)subDrivers.Count / candidate.SubDriverCount);
            }

            if (subDrivers.Count == 0)
                return null;

            int totalSubsProvided = driversNeeded * candidate.SubDriverCount;

            bool manufacturerMatch = !string.IsNullOrWhiteSpace(candidate.Manufacturer)
                && fixtureManufacturers.Contains(candidate.Manufacturer);

            return new CandidateEvaluation
            {
                Candidate = candidate,
                DriversNeeded = driversNeeded,
                TotalSubsProvided = totalSubsProvided,
                ManufacturerMatch = manufacturerMatch,
                SubDrivers = subDrivers,
                DriverIndicesPreAssigned = driverIndicesPreAssigned
            };
        }

        /// <summary>
        /// Compare two candidate evaluations. Returns true if eval is better than current.
        /// Priority: fewest drivers, fewest total subs, manufacturer match, fewer subs per unit
        /// </summary>
        private bool IsBetterCandidate(CandidateEvaluation eval, CandidateEvaluation current)
        {
            // 1. Manufacturer match
            if (eval.ManufacturerMatch != current.ManufacturerMatch)
                return eval.ManufacturerMatch;

            // 2. Fewest physical drivers
            if (eval.DriversNeeded != current.DriversNeeded)
                return eval.DriversNeeded < current.DriversNeeded;

            // 3. Fewest total sub-drivers provided
            if (eval.TotalSubsProvided != current.TotalSubsProvided)
                return eval.TotalSubsProvided < current.TotalSubsProvided;

            // 4. Fewer sub-drivers per unit (less waste)
            return eval.Candidate.SubDriverCount < current.Candidate.SubDriverCount;
        }

        /// <summary>
        /// Recursively split a fixture into segments that fit within maxSubDriverWattage
        /// </summary>
        private List<FixtureSegment> SplitFixture(FixtureData fixture, double maxSubDriverWattage)
        {
            double wattage = fixture.EffectiveWattage;

            if (wattage <= maxSubDriverWattage)
            {
                return new List<FixtureSegment>
                {
                    new FixtureSegment
                    {
                        FixtureId = fixture.FixtureId,
                        TypeMark = fixture.TypeMark,
                        Wattage = wattage,
                        IsSplit = false,
                        OriginalWattage = wattage,
                        SplitLabel = null,
                        LinearLength = fixture.LinearLength
                    }
                };
            }

            return SplitRecursive(fixture.FixtureId, fixture.TypeMark, wattage, wattage, fixture.LinearLength, maxSubDriverWattage);
        }

        private List<FixtureSegment> SplitRecursive(
            Autodesk.Revit.DB.ElementId fixtureId, string typeMark,
            double wattage, double originalWattage, double linearLength,
            double maxSubDriverWattage)
        {
            if (wattage <= maxSubDriverWattage)
            {
                return new List<FixtureSegment>
                {
                    new FixtureSegment
                    {
                        FixtureId = fixtureId,
                        TypeMark = typeMark,
                        Wattage = wattage,
                        IsSplit = true,
                        OriginalWattage = originalWattage,
                        SplitLabel = null,
                        LinearLength = linearLength
                    }
                };
            }

            double half = wattage / 2.0;
            double halfLength = linearLength / 2.0;
            var left = SplitRecursive(fixtureId, typeMark, half, originalWattage, halfLength, maxSubDriverWattage);
            var right = SplitRecursive(fixtureId, typeMark, half, originalWattage, halfLength, maxSubDriverWattage);
            var combined = new List<FixtureSegment>();
            combined.AddRange(left);
            combined.AddRange(right);
            return combined;
        }

        /// <summary>
        /// Pack segments into sub-drivers respecting a maximum fixtures per driver constraint.
        /// Fixtures are batched into groups of maxFixturesPerDriver, and each batch is packed independently.
        /// </summary>
        private List<SubDriverAssignment> PackSegmentsWithFixtureLimit(
            List<FixtureSegment> allSegments,
            double maxSubDriverWattage,
            int maxFixturesPerDriver,
            int subDriversPerDriver,
            out int driversNeeded)
        {
            var fixtureGroups = allSegments
                .GroupBy(s => s.FixtureId)
                .OrderByDescending(g => g.Sum(s => s.Wattage))
                .ToList();

            var allSubDrivers = new List<SubDriverAssignment>();
            int currentDriverIndex = 1;

            for (int i = 0; i < fixtureGroups.Count; i += maxFixturesPerDriver)
            {
                var batchSegments = fixtureGroups
                    .Skip(i)
                    .Take(maxFixturesPerDriver)
                    .SelectMany(g => g)
                    .ToList();

                var batchSubDrivers = PackSegments(batchSegments, maxSubDriverWattage);

                int batchDriverCount = (int)Math.Ceiling((double)batchSubDrivers.Count / subDriversPerDriver);

                for (int j = 0; j < batchSubDrivers.Count; j++)
                {
                    batchSubDrivers[j].SubDriverIndex = allSubDrivers.Count + j + 1;
                    batchSubDrivers[j].DriverIndex = currentDriverIndex + (j / subDriversPerDriver);
                    batchSubDrivers[j].Capacity = maxSubDriverWattage;
                }

                currentDriverIndex += batchDriverCount;
                allSubDrivers.AddRange(batchSubDrivers);
            }

            driversNeeded = currentDriverIndex - 1;
            return allSubDrivers;
        }

        /// <summary>
        /// Pack segments into sub-drivers using First-Fit Decreasing
        /// </summary>
        private List<SubDriverAssignment> PackSegments(List<FixtureSegment> segments, double maxSubDriverWattage)
        {
            AssignSplitLabels(segments);

            var sorted = segments.OrderByDescending(s => s.Wattage).ToList();
            var subDrivers = new List<SubDriverAssignment>();

            foreach (var segment in sorted)
            {
                SubDriverAssignment target = null;
                foreach (var sd in subDrivers)
                {
                    if (sd.TotalLoad + segment.Wattage <= maxSubDriverWattage)
                    {
                        target = sd;
                        break;
                    }
                }

                if (target == null)
                {
                    target = new SubDriverAssignment
                    {
                        SubDriverIndex = subDrivers.Count + 1,
                        TotalLoad = 0,
                        Capacity = maxSubDriverWattage,
                        Segments = new List<FixtureSegment>()
                    };
                    subDrivers.Add(target);
                }

                target.Segments.Add(segment);
                target.TotalLoad += segment.Wattage;
            }

            return subDrivers;
        }

        private void AssignSplitLabels(List<FixtureSegment> segments)
        {
            var splitGroups = segments
                .Where(s => s.IsSplit)
                .GroupBy(s => s.FixtureId);

            foreach (var group in splitGroups)
            {
                int index = 0;
                foreach (var segment in group)
                {
                    char label = (char)('A' + index);
                    segment.SplitLabel = label.ToString();
                    index++;
                }
            }
        }

        private class CandidateEvaluation
        {
            public DriverCandidateInfo Candidate { get; set; }
            public int DriversNeeded { get; set; }
            public int TotalSubsProvided { get; set; }
            public bool ManufacturerMatch { get; set; }
            public List<SubDriverAssignment> SubDrivers { get; set; }
            public bool DriverIndicesPreAssigned { get; set; }
        }
    }
}
