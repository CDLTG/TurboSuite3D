# TurboDALI

Standalone modeless command that owns the DALI lighting-control system end to end: collect DALI fixtures, group their Control Zones into **loops**, declare each loop's panel **ZONE**, then assign and **write per-unit addresses** back onto the model — one address per addressable unit (a driver device, or a self-driven downlight fixture) — with a job-wide numbering lock for final submittal. TurboZones is a pure downstream consumer of the result (module count + panel placement); it no longer edits DALI.

**Gated.** Registered only when `ExperimentalCommandsEnabled` is set (`Shim/App/TurboSuiteApplication.cs`), alongside TurboDMX. While gated, TurboDALI is the *only* DALI editing surface, so DALI declaration is dev-only until the gate ungates. The window is modeless (`TurboNumber`/`TurboZones`/`TurboDMX` pattern): the open-time read and every write route through an `IExternalEventHandler` work queue so all Revit API calls run on the API thread.

The model to mirror is **TurboDMX** — a standalone module owning its full workflow and feeding TurboZones through the one `IControlSubsystemDemandProvider` seam. Much of TurboDALI ports from DMX *by copy, not shared reference* (the numbering-lock reconciler, the zone-color overlay, the window chrome); the copies are re-scoped to DALI so both subsystems coexist.

## Workflow

1. **Pool → loops.** The left pool lists the job's unassigned Control Zones (each `{zone} ({loadCount})`). Multi-select and **New loop from selection**, or **New empty loop** and **+ Add selected**. A loop is one DALI bus = one `LQSE2-1DALUNV-D` module; a Control Zone lives in the pool or in exactly one loop. **Drag a loop's member zones (grab the zone name) to reorder them** — the member-zone order is the outer addressing key (see *Ordering* below), so reordering re-sequences the addresses; drops are same-loop only.
2. **Assign a ZONE.** Each loop needs the panel **ZONE N** its module sits in (the dropdown is model-derived from the discovered panel zones — `PanelAllocationService.DiscoverPanelZones`). A loop left unassigned is still ordered on the BOM, just not placed; it warns. Over **64 loads/bus** warns too.
3. **Write addresses ▸.** Computes each addressable unit's `L{loop#}-{short##}` label and stamps it onto that unit's **one** element — the driver device (driver unit) or the self-driven fixture (downlight unit) — clearing any stale labels (a driver's tape fixtures carry none). This is DALI's "Place": writing the computed identity into the model *is* the placement (no family is dropped).
4. **Lock / Re-lock / Unlock.** At final submittal, **Lock** freezes every issued address as a baseline; later writes only append new loads and surface REVIEWs for anything that would relabel an issued address. **Unlock** discards the baseline back to free renumbering.

Loop edits auto-save (coalesced), so the window reopens where you left it.

## The address

A **string**, form `L{loop#}-{short##}` (e.g. `L2-00`), where `##` **is** the DALI hardware short address (0–63), **zero-based** (the first unit on a bus is `-00`). Written to the shared **"DALI Address"** instance parameter (`ParameterNames.DaliAddress`), bound to **both Lighting Fixtures and Lighting Devices** so the driver device carries it too. Authored shared (taggable) so a future purpose-built tag family can read it — TurboDALI itself does not tag.

**Ordering (why it's spatial).** The addresses land as plan tags eventually, so their sequence must read the way a plan reads. Within a loop the canonical order is **member-zone declared order (outer) → NW-seeded proximity walk over the circuits (inner)**: each zone is a contiguous block (matching the Lutron software downstream), and within a zone the circuits are walked NW-first (max Y, then min X seed) by their **fixture centroid**, with each driver-bearing circuit **expanded into its several driver units in ordinal (down-column) order** at that walked spot. One uniform walk — a mixed zone (downlights + drivers) needs no special block split; a driver circuit simply emits N contiguous addresses where its tape sits. The walk is ported from TurboWire's `FixtureOrderingService` into the pure, Revit-free `Core/Dali/Addressing/ProximityWalk.cs` (double-farthest-point diameter → greedy nearest-neighbor → shorter path), unit-tested over synthetic layouts.

**One address = one unit, keyed durably.** The addressing/counting grain is the **addressable unit**, not the circuit: a circuit carrying N drivers is N addresses (one per driver device); a self-driven downlight circuit is one. The durable key is **`circuit.UniqueId#ordinal`** for a driver (the circuit isn't recreated on a redeploy; the ordinal is the driver's Switch ID *suffix* — the base is a non-unique placeholder, so only the suffix counts) and the **fixture UniqueId** for a downlight. Drivers on one circuit that collide on an ordinal (blank/hand-edited Switch IDs) are disambiguated so no address is silently dropped, and a warning is raised (give each a distinct Switch ID — run TurboNumber — for redeploy-stable addressing). Uncircuited DALI fixtures are **warned-and-excluded**, never addressed (circuit them first) — the window reports the skipped count.

## The numbering lock

Job-wide (a flag + a frozen baseline snapshot), the two-level analog of `Core/Dmx/Lock/DmxLockReconciler`, because a DALI address is *two* numbers where DMX's DEC# is one. `Core/Dali/Addressing/DaliAddressReconciler.cs` runs the Fresh/Pinned reconcile nested — loop level (anchor `LoopId`) then short-address level (anchor the durable **unit key**):

- **Unlocked** — fresh `L1..LN` / `00..` every write, from the live spatial walk. Nothing is issued; addresses churn freely.
- **Locked** — each loop keeps its `L#`, each unit keeps its slot; a new loop/unit appends past the high-water; a deleted one **gaps** (no reuse — a retired number is never re-offered while locked). A driver redeploy that keeps the same circuit + ordinals keeps its addresses (the key survives it).
- **REVIEW** (surfaced, never applied silently — the DMX rule): a locked unit that **moved loops** (its `L#` is now wrong → re-issued and flagged) or **was deleted** (address retired). A unit moving zones *within* the same loop keeps a valid address → silent until the next unlock+re-walk. The header banner turns amber and the REVIEW list names each affected address (read warnings — e.g. a shared-suffix circuit — ride the same list).

The reconciler's doc-comment proves why the per-zone high-water the plan describes collapses to the loop high-water under the no-reuse invariant — read it before touching the append math.

## Persistence

Rides the existing DALI schema (GUID `26ac35a5-…`, `TurboSuiteDaliModule`) — a JSON blob in one ES field, so shape changes bump `PayloadVersion`, not the GUID. **v3** added the `DaliSnapshotDto` (the lock flag + per-loop `L#` + per-**circuit** slot). **v4** re-anchors the snapshot's slots from the circuit to the **addressable unit** (`DaliSnapshotUnitDto`, keyed by unit key) — same field set, so still a `PayloadVersion` bump, not a new GUID. A pre-v4 baseline keys on circuit UniqueIds the reconciler can no longer pin, so it is **discarded on load** (`DaliPayload.DiscardStaleSnapshot`): the declared loops survive, the job reverts to Unlocked, and the designer re-locks. Tolerant read otherwise: a v2 payload upgrades in code to unlocked/unaddressed.

**Two writers, one schema, merge-preserving store.** The loop auto-save and the lock write the *same* schema but *different* fields, so `DaliLoopStore` (`Shim/Dali/Services/`) makes both **read-modify-write**: `Save` writes only the loops and keeps the persisted snapshot; `SaveSnapshot` writes only the snapshot and keeps the loops. Both run on the one work queue, so their load/store pairs never interleave.

## Zone color overlay

While the window is open, the active view's DALI fixtures are colored by **Control Zone** (a view aid for telling zones apart while pooling/grouping — not a deliverable), reverting on close. A near-verbatim copy of DMX's palette + contract + shim (`DaliZonePalette` / `IDaliZoneColorService` / `DaliZoneColorService`), re-scoped to `Dimming Protocol = DALI` so it never cross-colors DMX fixtures that share a zone name. The driver/decoder **devices** ride along in the same color — a device carries no Control Zone of its own, so its color is **derived from its circuit's zone** (the zone of the DALI fixtures sharing `circuit.Elements`, the same linkage the address writer uses), never written. So a driver reads out the color of the fixtures it drives. Direct per-element `View.SetElementOverrides` (works under a view template, unlike filters), remembered per-view so Revert clears exactly what it applied. Close defers to revert the overlay on the API thread first (`ModelessWindowGuard` force-close skips it — the closing doc's overrides go with it).

## Layout

- **Pure engine, unit-tested** — `Core/Dali/`: `Addressing/` (reconciler, proximity walk, snapshot builder, models), `Overlay/DaliZonePalette`, `Persistence/DaliPersistenceModels`, `Input/` (`DaliLoadCounter` demand count + `DaliPlacementMapper` placement map, both consumed by TurboZones), `DaliSolver` (loops → `ControlSubsystemDemand`), `ViewModels/` (`DaliMainViewModel` addressing+lock, `DaliTabViewModel` loop declaration), `Services/` (the Revit-free seams).
- **Revit-coupled** — `Shim/Dali/`: `DaliCommand` (modeless entry), `Views/TurboDaliWindow.xaml` (DMX chrome, three-column body), `Services/` (`DaliUnitEnumerator` the one shared unit walk, `DaliModelReader` addressing read + `DaliDemandProvider` count read both riding it, `DaliAddressWriter` per-unit write-back, `DaliZoneColorService`, `DaliLoopStore`, `DaliStorageService`, `DaliTabInputProvider`).

## Gotchas

- **`LoopId` is the durable L# anchor** — a creation-time GUID, not the display name. `DaliTabViewModel` restores the persisted `LoopId` on load (do not let it mint a fresh GUID per reload, or the lock orphans every loop).
- **DALI circuits are unassigned** (panel `<None>`; Revit reports the default number `<unnamed>`, so there is no meaningful circuit number). `circuit.UniqueId` is the stable per-circuit handle, and it forms the driver unit key (`circuit.UniqueId#ordinal`); the address is written per unit, onto the driver device / self-driven fixture.
- **Centroid uses fixtures only** — a driver device sits at an arbitrary ceiling spot ("Field Locate Me"), so it never enters the walk; the circuit's fixture centroid is the walk anchor and its driver units order by Switch ID suffix ordinal within it. Write-back targets the unit's **one** element (the driver device, or the self-driven fixture) — not the tape fixtures.
- **Unassigned loops write nothing** (ordered but sited nowhere) and surface as a warning, not an address.
