#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Implements the driver selection algorithm using real project driver types
    /// </summary>
    public class DriverSelectionService
    {
        /// <summary>
        /// Get the recommended driver configuration for a list of fixtures,
        /// evaluating each valid driver candidate from the project.
        /// </summary>
        /// <param name="links">The project's fixture-type → driver-family links (TurboRPS → Driver
        /// Match). When the circuit's fixtures are linked, only the linked family's types are
        /// candidates — a hard restriction with no fallback to other families or TBD. When unlinked,
        /// the result reports a tie (<see cref="DriverRecommendation.TiedCandidates"/>) if a different
        /// family equals the winner on every ranking criterion. Null = no links.</param>
        public DriverRecommendation GetRecommendation(
            List<FixtureData> fixtures,
            List<DriverCandidateInfo> driverCandidates,
            DriverLinks links = null)
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

            // Links: a linked fixture type restricts the pool to its family. Every linked fixture
            // on the circuit must agree; unlinked fixtures riding along defer to the link.
            var linkedFamilies = new List<ElementRef>();
            if (links != null && links.Count > 0)
            {
                foreach (var f in fixturesWithPower)
                {
                    if (links.TryGetFamily(f.TypeRef, out var fam) && !linkedFamilies.Contains(fam))
                        linkedFamilies.Add(fam);
                }
            }

            if (linkedFamilies.Count > 1)
            {
                string names = string.Join(", ", linkedFamilies.Select(fam => FamilyNameOf(fam, driverCandidates)));
                return NoMatchRecommendation(
                    $"Fixture types on this circuit are linked to different driver families ({names}) — " +
                    "fix the links in TurboRPS → Driver Match.");
            }

            string linkedFamilyName = null;
            if (linkedFamilies.Count == 1)
            {
                var linked = linkedFamilies[0];
                linkedFamilyName = FamilyNameOf(linked, driverCandidates);
                validCandidates = validCandidates
                    .Where(c => !c.IsTbd && c.FamilyRef == linked)
                    .ToList();
            }

            // Evaluate each valid candidate. Every evaluation is retained: tie detection needs to
            // look back at the ones that equalled the winner.
            CandidateEvaluation bestEval = null;
            var evaluations = new List<CandidateEvaluation>();

            foreach (var candidate in validCandidates)
            {
                var eval = EvaluateCandidate(candidate, fixturesWithPower, fixtureManufacturers, fixtureDimmingProtocols, fixtureVoltages);
                if (eval == null)
                    continue;

                evaluations.Add(eval);
                if (bestEval == null || IsBetterCandidate(eval, bestEval))
                {
                    bestEval = eval;
                }
            }

            if (bestEval == null)
            {
                if (linkedFamilyName == null)
                    return NoMatchRecommendation();

                string hint = fixtureVoltages.Count == 0 || fixtureDimmingProtocols.Count == 0
                    ? " The fixtures don't declare both Voltage and Dimming Protocol yet."
                    : string.Empty;
                var noFit = NoMatchRecommendation(
                    $"Linked family {linkedFamilyName} has no type that can serve this circuit.{hint}");
                noFit.LinkedFamilyName = linkedFamilyName;
                return noFit;
            }

            var best = bestEval;

            // Tie: on an unlinked circuit, a real candidate from a DIFFERENT family that is neither
            // better nor worse than the winner. TBD never ties (it only wins when nothing real fits).
            // Same-family ties are not ties here — TurboDriver keeps sizing within the family.
            var tied = new List<DriverCandidateInfo>();
            if (linkedFamilyName == null && !best.Candidate.IsTbd)
            {
                tied.Add(best.Candidate);
                var seenFamilies = new HashSet<string>(StringComparer.Ordinal) { FamilyKey(best.Candidate) };
                foreach (var eval in evaluations)
                {
                    if (ReferenceEquals(eval, best) || eval.Candidate.IsTbd)
                        continue;
                    if (IsBetterCandidate(eval, best) || IsBetterCandidate(best, eval))
                        continue;
                    if (seenFamilies.Add(FamilyKey(eval.Candidate)))
                        tied.Add(eval.Candidate);   // first per family ⇒ deterministic
                }
                if (tied.Count < 2)
                    tied.Clear();
            }

            // Assign driver indices to sub-drivers (unless already assigned by fixture-limited packing)
            if (!best.DriverIndicesPreAssigned)
            {
                for (int i = 0; i < best.SubDrivers.Count; i++)
                {
                    best.SubDrivers[i].DriverIndex = (i / best.Candidate.SubDriverCount) + 1;
                    best.SubDrivers[i].Capacity = best.Candidate.SubDriverPower;
                }
            }

            string catalogNumber = best.Candidate.CatalogNumber ?? "";
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
                WarningMessage = null,
                TiedCandidates = tied,
                LinkedFamilyName = linkedFamilyName
            };
        }

        /// <summary>Family identity for tie grouping: the Family element when known, else its name
        /// (callers without element handles still group same-named types as one family).</summary>
        private static string FamilyKey(DriverCandidateInfo c)
            => c.FamilyRef.IsValid
                ? "#" + c.FamilyRef.Value
                : "n:" + (c.FamilyName ?? string.Empty).ToUpperInvariant();

        private static string FamilyNameOf(ElementRef family, List<DriverCandidateInfo> candidates)
        {
            var match = candidates?.FirstOrDefault(c => c.FamilyRef == family && !string.IsNullOrEmpty(c.FamilyName));
            return match?.FamilyName ?? "(unknown family)";
        }

        /// <summary>
        /// Unified "no driver" result. Returned whenever no real driver matches and no TBD
        /// placeholder is present in the project — or when a link restricts the circuit to a family
        /// that can't serve it, or two links conflict (<paramref name="warning"/> says which). No
        /// fabricated fallback — the circuit fails loudly.
        /// </summary>
        private static DriverRecommendation NoMatchRecommendation(string warning = null)
        {
            return new DriverRecommendation
            {
                DriverCount = 0,
                DriverType = null,
                SubDriversPerDriver = 0,
                TotalSubDrivers = 0,
                DisplayText = "No matching driver",
                SubDriverAssignments = new List<SubDriverAssignment>(),
                RecommendedCandidate = null,
                HasMatch = false,
                WarningMessage = warning ?? "No matching driver — specify a driver type or add a TBD placeholder."
            };
        }

        private CandidateEvaluation EvaluateCandidate(
            DriverCandidateInfo candidate,
            List<FixtureData> fixturesWithPower,
            HashSet<string> fixtureManufacturers,
            HashSet<string> fixtureDimmingProtocols,
            HashSet<string> fixtureVoltages)
        {
            // A real (non-TBD) driver may only serve a circuit that has declared every
            // discriminating spec the matcher filters on — today Voltage AND Dimming
            // Protocol. Until both are present the circuit is underspecified, so no real
            // driver is eligible and it falls through to the TBD placeholder. The gate
            // fields below MUST stay in lockstep with the match checks: if a new hard
            // filter is ever added, add its "declared?" guard here too. TBD is the
            // wildcard and skips all of this.
            if (!candidate.IsTbd)
            {
                // Not enough information yet → defer to TBD (or fail if no TBD exists).
                if (fixtureVoltages.Count == 0 || fixtureDimmingProtocols.Count == 0)
                    return null;

                // A blank field on a real driver is NOT a wildcard — it must declare and
                // match the circuit's value.
                if (string.IsNullOrWhiteSpace(candidate.Voltage)
                    || !fixtureVoltages.Contains(candidate.Voltage))
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(candidate.DimmingProtocol)
                    || !fixtureDimmingProtocols.Contains(candidate.DimmingProtocol))
                {
                    return null;
                }
            }

            // Apply derating only to the packing ceiling. SubDriverCount / IsValidDriver
            // were computed from the rated SubDriverPower upstream and must not be derated.
            double maxSubWattage = candidate.SubDriverPower * candidate.DerateFactor;

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
        /// Compare two candidate evaluations. Returns true if eval is strictly better than
        /// current. Priority: real-over-TBD, manufacturer match, fewest drivers, fewest total
        /// subs, fewer subs per unit, then least overkill (smallest-rated unit that still fits).
        /// Every criterion is a strict inequality, so a full tie keeps <paramref name="current"/>
        /// (the earlier-encountered candidate) — the comparison stays order-stable.
        /// </summary>
        private bool IsBetterCandidate(CandidateEvaluation eval, CandidateEvaluation current)
        {
            // 0. Last resort: a real driver always beats the TBD placeholder. TBD only
            // wins when it is the sole survivor (nothing real matched the circuit).
            if (eval.Candidate.IsTbd != current.Candidate.IsTbd)
                return !eval.Candidate.IsTbd;

            // 1. Manufacturer match
            if (eval.ManufacturerMatch != current.ManufacturerMatch)
                return eval.ManufacturerMatch;

            // 2. Fewest physical drivers
            if (eval.DriversNeeded != current.DriversNeeded)
                return eval.DriversNeeded < current.DriversNeeded;

            // 3. Fewest total sub-drivers provided
            if (eval.TotalSubsProvided != current.TotalSubsProvided)
                return eval.TotalSubsProvided < current.TotalSubsProvided;

            // 4. Fewer sub-drivers per unit (less waste). Note: once DriversNeeded and
            // TotalSubsProvided both tie this is necessarily equal too (TotalSubs =
            // DriversNeeded × SubDriverCount), so it decides nothing on its own — it stays
            // as a guard in case an earlier criterion is ever relaxed.
            if (eval.Candidate.SubDriverCount != current.Candidate.SubDriverCount)
                return eval.Candidate.SubDriverCount < current.Candidate.SubDriverCount;

            // 5. Least overkill: among units that pack the load equally well (same physical
            // count, same total sub-drivers), prefer the smallest-rated one. This is what makes
            // a 30 W circuit land on the 100 W transformer instead of the 1000 W — without it,
            // every single-unit type ties on the criteria above and the winner is decided by
            // candidate order (which is lexical by type name, and "1000W" sorts before "100W").
            // Derating needs no special handling here: a unit too small for the load AT its
            // derated ceiling can't hold it in one sub-driver, so it splits and loses on
            // DriversNeeded (criterion 2) long before reaching this tie-breaker — e.g. a 225 W
            // load at 80 % rejects the 250 W unit (250×0.8 = 200 < 225 ⇒ 2 subs) and lands on
            // the 300 W (300×0.8 = 240 ≥ 225 ⇒ 1 sub).
            const double powerTolerance = 0.01;
            if (Math.Abs(eval.Candidate.SubDriverPower - current.Candidate.SubDriverPower) > powerTolerance)
                return eval.Candidate.SubDriverPower < current.Candidate.SubDriverPower;

            return false;
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
            ElementRef fixtureId, string typeMark,
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
