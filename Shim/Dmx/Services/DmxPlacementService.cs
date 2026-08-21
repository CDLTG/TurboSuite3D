#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Dmx.Helpers;
using TurboSuite.Dmx.Placement;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Shim-side <see cref="IDmxPlacementService"/> — the first TurboDMX model writes.
    /// Loop-by-loop click-to-place, mirroring TurboDriver's deploy gesture: for each loop prompt one point,
    /// drop that loop's decoder + driver instances in a tidy two-column strip (cosmetic layout deferred per
    /// ), write the decoder <c>Switch ID</c> ("DEC n"), and place real auto-syncing tags — a
    /// Switch-ID tag on each decoder, a Type-Mark tag on each driver (the driver's Type Mark is NOT written,
    /// only tagged — the family already carries it, e.g. "MD"; per the chosen Phase-2 behavior).
    ///
    /// Placement is non-destructive: a re-Place lands only the unbuilt remainder (skips
    /// DEC #s already in the model) and removes ORPHANS — pairs whose DEC # left the solve — using the
    /// persisted registry to delete the decoder AND its paired driver exactly (auto when Unlocked, confirmed
    /// when Locked). This preserves the designer's click-placement + any manual switch systems on survivors,
    /// while still reconciling removals (Option A, decided 2026-06-26).
    ///
    /// After placement, a circuiting pass creates one <c>&lt;unnamed&gt;</c> (unpaneled) power circuit per
    /// <b>Control Zone</b> — all the zone's DMX fixtures + all its decoders + all its drivers — because a
    /// Control Zone is one control behaviour = one address = one load (a zone's several decoders are power
    /// subdivision under that one address, not separate loads). So the Load Schedule sees one row per zone with
    /// the zone's tape load, and TurboZones assigns one Load Name per zone. It reconciles the whole system (tear
    /// down changed/orphaned circuits, then create), preserving Load Names across a rebuild. Each loop places in
    /// its own transaction, committed before the next pick (you can't pick inside an open transaction). Escape
    /// during any pick stops the run and keeps what's already placed.
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

        public DmxPlacementResult Place(DmxPlacementPlan plan, bool locked, IReadOnlyList<DmxPlacedPair> registry,
                                        int? onlyInterfaceNumber = null)
        {
            var result = new DmxPlacementResult();
            if (plan == null || plan.LoopCount == 0) return result;

            var placedIds = new List<ElementId>();
            View view = _doc.ActiveView;
            double? displayZ = GetDisplayElevation(_doc, view);

            // Option-A cleanup: remove decoders whose DEC # is no longer in the solve, plus their
            // paired drivers (exact, via the registry) — auto when Unlocked, confirmed when Locked. Runs in
            // its own committed transaction BEFORE the remainder scan so freed DEC #s aren't seen as "existing".
            RemoveOrphans(plan, locked, registry, result);

            // Idempotent re-Place ( "unbuilt remainder"): skip any decoder whose DEC # is already in
            // the model, so placing again after a locked additive re-run lands only the new decoders.
            var existing = ExistingDecoderSwitchIds();

            foreach (var loop in plan.Loops)
            {
                // Per-loop Place (loop-centric): only the targeted interface is picked + placed this run.
                if (onlyInterfaceNumber.HasValue && loop.InterfaceNumber != onlyInterfaceNumber.Value) continue;

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

            // Circuiting pass — after the WHOLE loop (every decoder committed; CircuitService opens its own
            // transaction so nothing may be open here). Whole-system scope like RemoveOrphans: reconciles every
            // Control Zone in the plan, not just this run's placements (heals AlreadyPlaced skips and pre-feature
            // models), and ignores onlyInterfaceNumber for the same reason.
            CircuitZones(plan, registry, result);

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
            // Same view-crop rule as TurboDriver's stack: align the two-column strip to the MODEL at
            // odd crop rotations (it tilts with the geometry on screen), but SNAP to screen down/left
            // at square rotations (0/90/180/270) with the devices rotated upright. Identity in an
            // un-rotated view, so production placements are unaffected.
            double cropAngle = ViewOrientationHelper.GetViewRotation(view);
            bool snapToScreen = ViewOrientationHelper.IsNearRightAngle(cropAngle);
            XYZ downUnit = snapToScreen ? ViewOrientationHelper.ScreenOffsetToModel(view, new XYZ(0, -1, 0)) : new XYZ(0, -1, 0);
            XYZ rightUnit = snapToScreen ? ViewOrientationHelper.ScreenOffsetToModel(view, new XYZ(1, 0, 0)) : new XYZ(1, 0, 0);
            double deviceRotation = snapToScreen ? cropAngle : 0.0;

            int row = 0;
            foreach (var dev in devices)
            {
                XYZ rowDown = downUnit * (row * RowSpacingFt);
                var decoderPt = origin + rowDown;                    // picked point anchors the decoder column
                var driverPt = decoderPt - rightUnit * DriverDxFt;   // driver sits one bay to the decoder's left
                row++;

                // Decoder: place → write Switch ID → tag (Switch ID).
                var decoder = PlaceSymbol(dev.DecoderTypeId, decoderPt, deviceRotation);
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
                var driver = PlaceSymbol(dev.DriverTypeId, driverPt, deviceRotation);
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

        // ── §2/§3 Circuiting: one <unnamed> power circuit per CONTROL ZONE = all the zone's DMX fixtures + all
        // its decoders + all its drivers. A Control Zone is one control address = one load; its several decoders
        // are power subdivision under that one address, so they share the ONE circuit (not one each). Whole-system
        // reconcile, two-phase (tear down all changed/orphaned circuits, THEN create) so a fixture that moved
        // zones is freed before any create — ElectricalSystem.Create THROWS (not null) on an already-circuited
        // member (spike B), and two-phase means we never hit it. Runs with no transaction open.
        //
        // Our circuits are identified by SHAPE, not a tag: an UNPANELED power circuit carrying ≥1 DMX fixture.
        // Foreign circuits (TurboWire / a user) are paneled, so they are never touched. Keying on fixtures (not
        // decoders) means an orphaned zone whose decoders were just deleted by RemoveOrphans is still recognized.
        private void CircuitZones(DmxPlacementPlan plan, IReadOnlyList<DmxPlacedPair> registry,
                                  DmxPlacementResult result)
        {
            // Plan: Control Zone → the DEC#s serving it (all loops).
            var decsByZone = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var loop in plan.Loops)
                foreach (var dev in loop.Devices)
                {
                    int dec = ParseDec(dev.SwitchId);
                    if (dec <= 0 || string.IsNullOrWhiteSpace(dev.ZoneName)) continue;
                    if (!decsByZone.TryGetValue(dev.ZoneName, out var list))
                        decsByZone[dev.ZoneName] = list = new List<int>();
                    list.Add(dec);
                }
            if (decsByZone.Count == 0) return;
            var planZones = new HashSet<string>(decsByZone.Keys, StringComparer.OrdinalIgnoreCase);

            // Live decoders by DEC#, driver ids by DEC# (this run's placements first, then persisted registry).
            var decoderByDec = new Dictionary<int, FamilyInstance>();
            foreach (var fi in LightingDevices())
            {
                int dec = ParseDec(fi.LookupParameter(ParameterNames.SwitchId)?.AsString());
                if (dec > 0 && !decoderByDec.ContainsKey(dec)) decoderByDec[dec] = fi;
            }
            var driverByDec = new Dictionary<int, long>();
            if (registry != null)
                foreach (var p in registry) if (p.DriverId != 0L) driverByDec[p.Dec] = p.DriverId;
            foreach (var p in result.PlacedPairs) if (p.DriverId != 0L) driverByDec[p.Dec] = p.DriverId;

            // Model: DMX fixtures grouped by Control Zone (scoped on Dimming Protocol = DMX).
            var fixturesByZone = new Dictionary<string, List<FamilyInstance>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fi in LightingFixtures())
            {
                if (!IsDmxFixture(fi)) continue;
                string zone = fi.LookupParameter(ParameterNames.ControlZone)?.AsString()?.Trim();
                if (string.IsNullOrEmpty(zone)) continue;
                if (!fixturesByZone.TryGetValue(zone, out var list))
                    fixturesByZone[zone] = list = new List<FamilyInstance>();
                list.Add(fi);
            }

            // Our existing circuits: unpaneled power circuits carrying ≥1 DMX fixture, grouped by that zone.
            var ourCircuitsByZone = new Dictionary<string, List<ElectricalSystem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var circuit in UnpaneledPowerCircuits())
            {
                string zone = GetCircuitDmxZone(circuit);
                if (zone == null) continue;
                if (!ourCircuitsByZone.TryGetValue(zone, out var list))
                    ourCircuitsByZone[zone] = list = new List<ElectricalSystem>();
                list.Add(circuit);
            }

            var toTearDown = new HashSet<ElementId>();
            var toCreate = new List<(string Zone, List<FamilyInstance> Members)>();
            var preservedLoadName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Orphan circuits: our circuits whose zone left the plan → tear down, no rebuild (frees their fixtures).
            foreach (var kv in ourCircuitsByZone)
                if (!planZones.Contains(kv.Key))
                    foreach (var c in kv.Value) toTearDown.Add(c.Id);

            // Plan zones: skip (already correct) / rebuild (changed, split, or a decoder missing) / create.
            foreach (var kv in decsByZone)
            {
                string zone = kv.Key;
                var decoders = kv.Value.Where(decoderByDec.ContainsKey).Select(d => decoderByDec[d]).ToList();
                if (decoders.Count == 0) continue;   // nothing placed for this zone yet

                fixturesByZone.TryGetValue(zone, out var fixtures);
                if (fixtures == null || fixtures.Count == 0)
                {
                    result.Warnings.Add($"Zone \"{zone}\": no live DMX fixtures resolved — no circuit created.");
                    continue;
                }

                // Foreign-circuit guard: a zone fixture already on a paneled (TurboWire/user) circuit — don't steal.
                if (fixtures.Any(IsOnForeignCircuit))
                {
                    result.Warnings.Add($"Zone \"{zone}\": a fixture is already on a paneled circuit — skipped (left as wired).");
                    continue;
                }

                var desiredIds = new HashSet<long>(fixtures.Select(f => f.Id.ToRef().Value));
                ourCircuitsByZone.TryGetValue(zone, out var existing);
                var existingList = existing ?? new List<ElectricalSystem>();

                // Idempotent skip: exactly one circuit, its fixtures already match, and every zone decoder is on it.
                if (existingList.Count == 1 &&
                    GetCircuitFixtureIds(existingList[0]).SetEquals(desiredIds) &&
                    AllOnCircuit(decoders, existingList[0]))
                    continue;

                // Otherwise → (re)create. Preserve one Load Name off a recognized existing circuit for this zone.
                foreach (var c in existingList)
                {
                    var ln = ParameterHelper.GetLoadName(c);
                    if (!string.IsNullOrEmpty(ln)) { preservedLoadName[zone] = ln; break; }
                }

                var members = new List<FamilyInstance>(fixtures);
                members.AddRange(decoders);
                foreach (var dec in kv.Value)
                    if (driverByDec.TryGetValue(dec, out var did) &&
                        _doc.GetElement(new ElementRef(did).ToElementId()) is FamilyInstance drv)
                        members.Add(drv);

                toCreate.Add((zone, members));
            }

            if (toCreate.Count == 0 && toTearDown.Count == 0) return;

            // Free EVERY member we're about to circuit: tear down any unpaneled circuit that holds one of them —
            // not just the zone circuits we recognized. This is what makes reconcile robust to a recognition gap
            // or a prior partial run (ElectricalSystem.Create THROWS on an already-circuited member, spike B), so
            // every to-create member must be un-circuited first. Foreign PANELED circuits are never scanned.
            var createMemberIds = new HashSet<ElementId>(toCreate.SelectMany(t => t.Members).Select(m => m.Id));
            if (createMemberIds.Count > 0)
                foreach (var circuit in UnpaneledPowerCircuits())
                    foreach (Element el in circuit.Elements)
                        if (createMemberIds.Contains(el.Id)) { toTearDown.Add(circuit.Id); break; }

            // Phase 1 — tear down all those circuits in one transaction, freeing every to-create member so no
            // create in Phase 2 ever meets an already-circuited member. (No manual Regenerate — the commit
            // regenerates, and a Regenerate outside a transaction is itself illegal.)
            if (toTearDown.Count > 0)
            {
                using var tx = new Transaction(_doc, "TurboDMX — Reconcile zone circuits (teardown)");
                tx.Start();
                try
                {
                    foreach (var id in toTearDown)
                        if (_doc.GetElement(id) != null) { _doc.Delete(id); result.CircuitsRemoved++; }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Circuit teardown failed — {ex.Message}");
                    if (tx.HasStarted()) tx.RollBack();
                    return;   // don't create against circuits we failed to free
                }
            }

            // Phase 2 — create the fresh <unnamed> per-zone circuits (CircuitService opens its own tx each call).
            foreach (var (zone, members) in toCreate)
            {
                ElectricalSystem circuit = null;
                try
                {
                    circuit = CircuitService.CreateCircuit(_doc, members, assignPanel: false,
                        preprocessor: new DmxCircuitFailurePreprocessor());
                }
                catch (Exception ex)
                {
                    // Create THROWS on an already-circuited member (spike B) — treat like the null return.
                    result.Warnings.Add($"Zone \"{zone}\": circuit create failed — {ex.Message}");
                    continue;
                }
                if (circuit == null)
                {
                    result.Warnings.Add($"Zone \"{zone}\": circuit create rejected the member set — skipped.");
                    continue;
                }
                result.CircuitsCreated++;
                if (preservedLoadName.TryGetValue(zone, out var ln)) RestoreLoadName(circuit, ln);
            }
        }

        private IEnumerable<FamilyInstance> LightingFixtures() =>
            new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

        private IEnumerable<ElectricalSystem> UnpaneledPowerCircuits() =>
            new FilteredElementCollector(_doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>()
                .Where(c => c.SystemType == ElectricalSystemType.PowerCircuit && c.BaseEquipment == null);

        private static bool IsDmxFixture(FamilyInstance fi) =>
            ParameterHelper.GetDimmingProtocol(fi).Trim().Equals("DMX", StringComparison.OrdinalIgnoreCase);

        /// <summary>The Control Zone of a circuit's first DMX fixture member, or null if it has none — used to
        /// recognize an unpaneled circuit as one of ours and which zone it belongs to.</summary>
        private static string GetCircuitDmxZone(ElectricalSystem circuit)
        {
            foreach (Element el in circuit.Elements)
                if (el is FamilyInstance fi &&
                    fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures &&
                    IsDmxFixture(fi))
                {
                    string zone = fi.LookupParameter(ParameterNames.ControlZone)?.AsString()?.Trim();
                    if (!string.IsNullOrEmpty(zone)) return zone;
                }
            return null;
        }

        /// <summary>True when the fixture is on a PANELED power circuit — one TurboWire or a user made. Our own
        /// per-zone circuits are unpaneled, so a fixture on ours reads as not-foreign.</summary>
        private static bool IsOnForeignCircuit(FamilyInstance fixture)
        {
            var systems = fixture.MEPModel?.GetElectricalSystems();
            if (systems == null) return false;
            foreach (ElectricalSystem s in systems)
                if (s.SystemType == ElectricalSystemType.PowerCircuit && s.BaseEquipment != null)
                    return true;
            return false;
        }

        /// <summary>The lighting-fixture members of a circuit as ids (excludes decoder/driver devices) — the
        /// grain the idempotent skip compares against the zone's fixture set.</summary>
        private static HashSet<long> GetCircuitFixtureIds(ElectricalSystem circuit)
        {
            var ids = new HashSet<long>();
            foreach (Element el in circuit.Elements)
                if (el is FamilyInstance fi &&
                    fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures)
                    ids.Add(fi.Id.ToRef().Value);
            return ids;
        }

        /// <summary>True when every one of <paramref name="devices"/> is a member of <paramref name="circuit"/>
        /// — the idempotent skip also requires the zone's decoders to already be on the circuit.</summary>
        private static bool AllOnCircuit(IEnumerable<FamilyInstance> devices, ElectricalSystem circuit)
        {
            var memberIds = new HashSet<ElementId>();
            foreach (Element el in circuit.Elements) memberIds.Add(el.Id);
            return devices.All(d => memberIds.Contains(d.Id));
        }

        /// <summary>Restore a preserved TurboZones Load Name onto a rebuilt circuit (its own transaction).</summary>
        private void RestoreLoadName(ElectricalSystem circuit, string loadName)
        {
            using var tx = new Transaction(_doc, "TurboDMX — Restore Load Name");
            tx.Start();
            try
            {
                circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME)?.Set(loadName);
                tx.Commit();
            }
            catch
            {
                if (tx.HasStarted()) tx.RollBack();
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

        private FamilyInstance PlaceSymbol(string typeId, XYZ point, double rotation)
        {
            if (string.IsNullOrEmpty(typeId)) return null;
            if (!(_doc.GetElement(typeId) is FamilySymbol symbol)) return null;

            if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }
            var instance = _doc.Create.NewFamilyInstance(point, symbol, StructuralType.NonStructural);
            if (Math.Abs(rotation) > 1e-9)
            {
                var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                ElementTransformUtils.RotateElement(_doc, instance.Id, axis, rotation);
            }
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
