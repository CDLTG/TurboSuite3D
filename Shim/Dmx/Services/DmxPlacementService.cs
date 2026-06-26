#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Dmx.Placement;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Shim-side <see cref="IDmxPlacementService"/> — the first TurboDMX model writes (BuildPlan Phase 2).
    /// Loop-by-loop click-to-place, mirroring TurboDriver's deploy gesture: for each loop prompt one point,
    /// drop that loop's decoder + driver instances in a tidy two-column strip (cosmetic layout deferred per
    /// Design §8a), write the decoder <c>Switch ID</c> ("DEC n"), and place real auto-syncing tags — a
    /// Switch-ID tag on each decoder, a Type-Mark tag on each driver (the driver's Type Mark is NOT written,
    /// only tagged — the family already carries it, e.g. "MD"; per the chosen Phase-2 behavior).
    ///
    /// Placement is non-destructive (BuildPlan Phase 3): a re-Place lands only the unbuilt remainder (skips
    /// DEC #s already in the model) and removes ORPHANS — pairs whose DEC # left the solve — using the
    /// persisted registry to delete the decoder AND its paired driver exactly (auto when Unlocked, confirmed
    /// when Locked). This preserves the designer's click-placement + any manual switch systems on survivors,
    /// while still reconciling removals (Option A, decided 2026-06-26).
    ///
    /// No wiring / circuits here — DMX decoders aren't power-circuited like RPS (that's the §0c feed half,
    /// deferred). Each loop places in its own transaction, committed before the next pick (you can't pick
    /// inside an open transaction). Escape during any pick stops the run and keeps what's already placed.
    ///
    /// Invoked through the work queue, so this runs on the Revit API thread (UIDocument.Selection picks +
    /// the placement transactions are both legal there).
    /// </summary>
    public sealed class DmxPlacementService : IDmxPlacementService
    {
        // Decoder Switch-ID tag is the same family TurboDriver uses (decoders are OST_LightingDevices too);
        // the driver Type-Mark tag is the firm's "(Type)" tag family (both confirmed against loaded families).
        private const string DecoderTagFamily = "AL_Tag_Lighting Device (SwitchID)";
        private const string DriverTagFamily = "AL_Tag_Lighting Device (Type)";

        private const double RowSpacingFt = 9.5 / 12.0;   // 9.5" between devices, like TurboDriver's column
        // Layout per device: Driver ← 2'-0" → Decoder. The picked point anchors the DECODER (the tagged
        // DEC #); the driver sits one bay (24") to its LEFT (−X).
        private const double DriverDxFt = 24.0 / 12.0;

        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private Dictionary<string, ElementId> _tagTypes;

        public DmxPlacementService(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
        }

        public DmxPlacementResult Place(DmxPlacementPlan plan, bool locked, IReadOnlyList<DmxPlacedPair> registry)
        {
            var result = new DmxPlacementResult();
            if (plan == null || plan.LoopCount == 0) return result;

            var placedIds = new List<ElementId>();
            View view = _doc.ActiveView;
            double? displayZ = GetDisplayElevation(_doc, view);

            // Option-A cleanup (Phase 3): remove decoders whose DEC # is no longer in the solve, plus their
            // paired drivers (exact, via the registry) — auto when Unlocked, confirmed when Locked. Runs in
            // its own committed transaction BEFORE the remainder scan so freed DEC #s aren't seen as "existing".
            RemoveOrphans(plan, locked, registry, result);

            // Idempotent re-Place (Phase 3 "unbuilt remainder"): skip any decoder whose DEC # is already in
            // the model, so placing again after a locked additive re-run lands only the new decoders.
            var existing = ExistingDecoderSwitchIds();

            foreach (var loop in plan.Loops)
            {
                var todo = loop.Devices
                    .Where(d => string.IsNullOrEmpty(d.SwitchId) || !existing.Contains(d.SwitchId))
                    .ToList();
                result.AlreadyPlaced += loop.Devices.Count - todo.Count;
                if (todo.Count == 0) continue;   // whole loop already placed — no pick, no churn

                XYZ origin;
                try
                {
                    var picked = _uidoc.Selection.PickPoint(
                        $"TurboDMX — pick a point for {loop.Label} ({todo.Count} decoder(s)); Esc to stop");
                    origin = new XYZ(picked.X, picked.Y, displayZ ?? picked.Z);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    result.Cancelled = true;
                    break;   // keep everything placed so far
                }

                using (var tx = new Transaction(_doc, $"TurboDMX — Place {loop.Label}"))
                {
                    tx.Start();
                    try
                    {
                        PlaceLoop(todo, origin, view, result, placedIds);
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"{loop.Label}: placement transaction failed — {ex.Message}");
                        if (tx.HasStarted()) tx.RollBack();
                    }
                }
            }

            if (placedIds.Count > 0)
                _uidoc.Selection.SetElementIds(placedIds);

            if (result.Failed > 0 || result.Warnings.Count > 0)
            {
                var sb = new StringBuilder();
                if (result.Failed > 0) sb.AppendLine($"Failed to place: {result.Failed}");
                foreach (var w in result.Warnings) sb.AppendLine(w);
                TaskDialog.Show("TurboDMX", sb.ToString());
            }

            return result;
        }

        private void PlaceLoop(IReadOnlyList<DmxDevicePlacement> devices, XYZ origin, View view,
                               DmxPlacementResult result, List<ElementId> placedIds)
        {
            int row = 0;
            foreach (var dev in devices)
            {
                var decoderPt = new XYZ(origin.X, origin.Y - row * RowSpacingFt, origin.Z);
                var driverPt = new XYZ(origin.X - DriverDxFt, origin.Y - row * RowSpacingFt, origin.Z);
                row++;

                // Decoder: place → write Switch ID → tag (Switch ID).
                var decoder = PlaceSymbol(dev.DecoderTypeId, decoderPt);
                if (decoder == null)
                {
                    result.Failed++;
                    result.Warnings.Add($"{dev.SwitchId}: decoder type \"{dev.DecoderName}\" is not loaded — skipped.");
                }
                else
                {
                    result.DecodersPlaced++;
                    placedIds.Add(decoder.Id);

                    if (SetSwitchId(decoder, dev.SwitchId)) result.SwitchIdsSet++;
                    else result.Warnings.Add($"{dev.SwitchId}: placed decoder but could not write Switch ID.");

                    if (TagDevice(decoder, view, DecoderTagFamily)) result.TagsPlaced++;
                    else result.Warnings.Add($"{dev.SwitchId}: decoder placed but tag family \"{DecoderTagFamily}\" not found.");
                }

                // Driver: place → tag (Type Mark, NOT written — the family carries it).
                var driver = PlaceSymbol(dev.DriverTypeId, driverPt);
                if (driver == null)
                {
                    result.Failed++;
                    result.Warnings.Add($"{dev.SwitchId}: driver type \"{dev.DriverName}\" is not loaded — skipped.");
                }
                else
                {
                    result.DriversPlaced++;
                    placedIds.Add(driver.Id);

                    if (TagDevice(driver, view, DriverTagFamily)) result.TagsPlaced++;
                    else result.Warnings.Add($"{dev.SwitchId}: driver placed but tag family \"{DriverTagFamily}\" not found.");
                }

                // Register the placed pair (DEC # → decoder/driver ids) so a later re-Place can remove it as
                // an orphan exactly — even if the layout's been nudged. Keyed on a successfully placed decoder.
                if (decoder != null)
                    result.PlacedPairs.Add(new DmxPlacedPair(
                        ParseDec(dev.SwitchId),
                        decoder.Id.ToRef().Value,
                        driver?.Id.ToRef().Value ?? 0L));
            }
        }

        // ── Option-A orphan cleanup: remove placed decoders whose DEC # left the solve ───────────────
        // Model-scan based (not registry-only) so decoders placed BEFORE the registry existed are caught too.
        // The paired driver is resolved from the registry when available (exact, survives layout nudges),
        // else best-effort by geometry (the driver sits one bay to the decoder's left).
        private void RemoveOrphans(DmxPlacementPlan plan, bool locked,
                                   IReadOnlyList<DmxPlacedPair> registry, DmxPlacementResult result)
        {
            var valid = new HashSet<int>(plan.Loops.SelectMany(l => l.Devices)
                .Select(d => ParseDec(d.SwitchId)).Where(n => n > 0));

            var orphans = LightingDevices()
                .Select(fi => new { Fi = fi, Dec = ParseDec(fi.LookupParameter(ParameterNames.SwitchId)?.AsString()) })
                .Where(x => x.Dec > 0 && !valid.Contains(x.Dec))
                .ToList();
            if (orphans.Count == 0) return;

            // Locked ⇒ confirm (issued numbers / possible manual switch systems); Unlocked ⇒ just clean up.
            if (locked && !ConfirmOrphanRemoval(orphans.Count)) return;

            var driverByDec = new Dictionary<int, long>();
            if (registry != null)
                foreach (var p in registry) if (p.DriverId != 0L) driverByDec[p.Dec] = p.DriverId;

            using var tx = new Transaction(_doc, "TurboDMX — Remove Orphaned Decoders");
            tx.Start();
            try
            {
                foreach (var o in orphans)
                {
                    var driverId = ResolveDriverId(o.Dec, o.Fi, driverByDec);

                    _doc.Delete(o.Fi.Id);
                    if (driverId != ElementId.InvalidElementId) _doc.Delete(driverId);
                    else result.Warnings.Add($"DEC {o.Dec}: removed decoder; couldn't identify its driver — remove it manually.");

                    result.RemovedDecoders++;
                    result.RemovedDecs.Add(o.Dec);
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Orphan cleanup failed — {ex.Message}");
                if (tx.HasStarted()) tx.RollBack();
                result.RemovedDecs.Clear();               // rolled back ⇒ keep the registry intact
                result.RemovedDecoders = 0;
            }
        }

        // Registry first (exact), then geometry: the lighting device nearest the decoder's expected driver
        // point (one bay to its left), excluding other decoders, within a tight tolerance.
        private ElementId ResolveDriverId(int dec, FamilyInstance decoder, Dictionary<int, long> driverByDec)
        {
            if (driverByDec.TryGetValue(dec, out var did))
            {
                var rid = new ElementRef(did).ToElementId();
                if (_doc.GetElement(rid) != null) return rid;
            }

            var loc = GeometryHelper.GetFixtureLocation(decoder);
            if (loc == null) return ElementId.InvalidElementId;
            var expected = new XYZ(loc.X - DriverDxFt, loc.Y, loc.Z);

            const double tol = 1.0;   // ft — tight, so a wrong driver isn't grabbed
            FamilyInstance best = null;
            double bestD = tol;
            foreach (var fi in LightingDevices())
            {
                if (fi.Id == decoder.Id) continue;
                if (ParseDec(fi.LookupParameter(ParameterNames.SwitchId)?.AsString()) > 0) continue; // skip decoders
                var l = GeometryHelper.GetFixtureLocation(fi);
                if (l == null) continue;
                double d = l.DistanceTo(expected);
                if (d < bestD) { bestD = d; best = fi; }
            }
            return best?.Id ?? ElementId.InvalidElementId;
        }

        private IEnumerable<FamilyInstance> LightingDevices() =>
            new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_LightingDevices)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

        private static bool ConfirmOrphanRemoval(int count) =>
            new TaskDialog("TurboDMX")
            {
                MainInstruction = "Remove orphaned decoders",
                MainContent = $"{count} placed decoder(s) are no longer in the solve, and numbering is Locked.\n\n"
                            + "Remove them and their paired drivers from the model?",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            }.Show() == TaskDialogResult.Yes;

        // "DEC 7" → 7. Requires the DEC prefix so a model scan never mistakes another lighting device's
        // Switch ID (e.g. an RPS "X01") for a decoder.
        private static int ParseDec(string switchId)
        {
            if (string.IsNullOrEmpty(switchId)) return 0;
            var s = switchId.Trim();
            if (!s.StartsWith("DEC", StringComparison.OrdinalIgnoreCase)) return 0;
            var digits = new string(s.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : 0;
        }

        // Switch IDs already on placed lighting devices — the committed DEC #s we must not duplicate. RPS
        // power supplies share the category but carry non-"DEC" Switch IDs, so they never collide with a plan #.
        private HashSet<string> ExistingDecoderSwitchIds()
        {
            var ids = new HashSet<string>();
            foreach (var fi in LightingDevices())
            {
                var v = fi.LookupParameter(ParameterNames.SwitchId)?.AsString();
                if (!string.IsNullOrEmpty(v)) ids.Add(v);
            }
            return ids;
        }

        private FamilyInstance PlaceSymbol(string typeId, XYZ point)
        {
            if (string.IsNullOrEmpty(typeId)) return null;
            if (!(_doc.GetElement(typeId) is FamilySymbol symbol)) return null;

            if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }
            var instance = _doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            _doc.Regenerate();
            return instance;
        }

        private static bool SetSwitchId(FamilyInstance instance, string switchId)
        {
            if (string.IsNullOrEmpty(switchId)) return false;
            var p = instance.LookupParameter(ParameterNames.SwitchId);
            if (p == null || p.IsReadOnly) return false;
            p.Set(switchId);
            return true;
        }

        private bool TagDevice(FamilyInstance instance, View view, string tagFamily)
        {
            if (_tagTypes == null) _tagTypes = ResolveTagTypes();
            if (!_tagTypes.TryGetValue(tagFamily, out var tagTypeId)) return false;

            var location = GeometryHelper.GetFixtureLocation(instance);
            if (location == null) return false;

            var tag = IndependentTag.Create(_doc, view.Id, new Reference(instance), false,
                TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, location);
            tag.ChangeTypeId(tagTypeId);
            tag.TagHeadPosition = location;
            return true;
        }

        private Dictionary<string, ElementId> ResolveTagTypes()
        {
            var byFamily = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            var tagTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_LightingDeviceTags)
                .Cast<FamilySymbol>();

            foreach (var fs in tagTypes)
                if (!byFamily.ContainsKey(fs.FamilyName))
                    byFamily[fs.FamilyName] = fs.Id;

            return byFamily;
        }

        // ── View-range elevation snap (decoders/drivers have no 3D geometry, so a point off the display
        // plane hides the device while its view-owned tag still draws — same RCP gotcha TurboDriver fixes). ──
        private static double? GetDisplayElevation(Document doc, View view)
        {
            if (!(view is ViewPlan plan)) return null;

            PlanViewPlane whichPlane = plan.ViewType == ViewType.CeilingPlan
                ? PlanViewPlane.TopClipPlane
                : PlanViewPlane.BottomClipPlane;

            PlanViewRange range = plan.GetViewRange();
            ElementId levelId = range.GetLevelId(whichPlane);
            Level level;
            if (levelId.Equals(PlanViewRange.LevelAbove)) level = AdjacentLevel(doc, plan.GenLevel, above: true);
            else if (levelId.Equals(PlanViewRange.LevelBelow)) level = AdjacentLevel(doc, plan.GenLevel, above: false);
            else level = doc.GetElement(levelId) as Level;

            return level == null ? (double?)null : level.Elevation + range.GetOffset(whichPlane);
        }

        private static Level AdjacentLevel(Document doc, Level from, bool above)
        {
            if (from == null) return null;
            double baseElev = from.Elevation;
            const double Tol = 1e-6;
            Level best = null;
            foreach (var el in new FilteredElementCollector(doc).OfClass(typeof(Level)))
            {
                if (!(el is Level lvl)) continue;
                double e = lvl.Elevation;
                if (above) { if (e > baseElev + Tol && (best == null || e < best.Elevation)) best = lvl; }
                else { if (e < baseElev - Tol && (best == null || e > best.Elevation)) best = lvl; }
            }
            return best;
        }
    }
}
