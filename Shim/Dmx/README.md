# TurboDMX

Standalone modeless command that owns the DMX-controlled RGBW LED tape/fixture subsystem end to end: read the tagged DMX fixtures, group their Control Zones into declared **loops**, solve the whole system (decoder selection + driver/power packing, addressing, interface/link/processor roll-up, 120 V breaker feeds), **place** the decoder/driver instances and per-zone circuits into the model, generate a one-line diagram per loop, and lock the numbering for submittal. TurboZones consumes only the result (interface count + link demand) through the one `IControlSubsystemDemandProvider` seam. Entry `DmxCommand.cs`; pure engine in `Core/Dmx/` (unit-tested in `Tests/`), Revit-coupled half in `Shim/Dmx/`.

**Gated.** Registered only when `ExperimentalCommandsEnabled` is set (`Shim/App/TurboSuiteApplication.cs`), alongside TurboDALI; the ribbon still uses the `Blank` placeholder icon. Feature-complete and live-tested — end-of-project polish only. Modeless (`TurboNumber`/`TurboZones` pattern): the initial model read + state load happen in `Execute` before the window opens; the Refresh re-read and the coalesced state save route through an `IExternalEventHandler` work queue so all Revit API calls run on the API thread. Launch is refused while a transaction is open (`doc.IsModifiable`).

TurboDMX is the **parent pattern** for TurboDALI — the numbering-lock reconciler, zone-color overlay, and window chrome were ported to DALI *by copy, not shared reference*. When the two diverge, this is the original.

## Canonical vocabulary

The containment ladder, overloaded-word glossary, and solve spine live on **`DmxSolver` (`Core/Dmx/DmxSolver.cs`)** — read that `remarks` block before touching any of this; other files cite its rung numbers. In brief, the spine is **Project → Processor → Link → Interface → DMX Loop → Decoder → DMX Fixture**, with three cross-cutting logical groupings:

- **Control Zone** — the *addressing* grain: the tapes + decoders sharing **one** mirrored DMX address. The one true human input (the native `Control Zone` instance param on the tape). DMX subsystem membership is `Dimming Protocol = DMX` (see CLAUDE.md); the fixture's `DMX Channels` gives the channel count.
- **Physical cluster** — the *decoder-packing* grain: runs close enough to share a decoder (one wall/cove). Orthogonal to the Control Zone — a decoder can't reach across the room, so decoders pack per cluster and sum up to the zone.
- **DMX address** — the start channel a Control Zone owns (e.g. `005`), a position in the interface's channels; not a ladder rung.

## Workflow (loop-centric window, `Core/Dmx/ViewModels/DmxMainViewModel.cs`)

1. **Declarations** — set the profile (channel ceiling: Lutron 32 / native 512, `DmxProfile`), Kind-2 settings, and curated decoder/driver pools.
2. **Pool → loops.** Zones start in the `ZonePool` (the engine auto-packs them geometry-blind); the designer pulls zones into declared **loops**, each a tree node owning its zones (and each zone its cluster sub-builder). A declared loop forces its zones onto **one interface = one one-line diagram**; an undeclared zone falls through to auto-packing.
3. **Bill** (right pane) — the always-on whole-system roll-up (interfaces / links / processors / breakers), complete only once every loop is declared.
4. **Place** — per loop, its own Place action + placement state (below).
5. **One-line** — per loop, generate/refresh its owned drafting view (below).
6. **Lock / Re-lock / Unlock** — freeze the issued DEC numbering for submittal (below). The destructive lock actions are Yes/No-gated in the shim.

Loop/cluster edits and curation auto-save (coalesced), so the window reopens where the designer left it.

## The solve (`DmxSolver.Solve`, pure)

A deterministic pure function of the `DmxContract` (flat declared knobs — decoder pool, driver pool, volts, ceiling, D4, breaker basis) + the tagged `ZoneDesign`s + optional `LoopDeclaration`s. No part is named in code; the bill is derived. Pipeline:

0. **Validate** (`DmxValidator`) — three pre-solve hard-stops, refusing before any partial bill and **never silently splitting a drawn run or a declared loop**: `UnmappableTapeException` (no decoder in the pool fits a zone's channels), `OverCapRunsException` (a correctly-drawn run over the ceiling), `OverCapLoopsException` (a declared loop summing past one interface ceiling — the cable break is the designer's geometry call).
1. **Power** — select a decoder type per zone (smallest whose outputs ≥ the zone's channels), then power-pack **per physical cluster** and sum.
2. **Control** — address zones and pack them into interfaces under the channel ceiling; each declared loop becomes one interface in declaration order, the rest auto-pack (`InterfacePacker`).
3. **Segment + feed** — split each loop into repeater-bounded signal segments by D4 (`LoopSegmenter`, ≤~32 devices / ≤1000 ft, vendor-independent physics), and breaker-pack its drivers onto 120 V feeds **per interface in DEC-walk order** (next-fit, never spanning interfaces) so the feed count equals the one-line's drawn `120V FEED` blocks. Per-driver watts are connected load or full nameplate per `BreakerBasis`.
4. **Roll-up** (report-only) — pack interfaces onto control links (512 legs / 16 devices, 1 channel = 1 leg), then links → processors (HQP7-2 = 2 links). **Sized and reported, never enforced or provisioned** — the QS link is shared with all non-DMX Lutron loads, so TurboDMX reports demand and lets TurboZones own the link budget.

## DEC numbering + the lock (`Core/Dmx/Lock/DmxLockReconciler.cs`)

Decoders are numbered `DEC n`, system-wide `1..N` (per-loop Place just stamps the numbers the whole-system solve already assigned). The lock baseline is **Control-Zone-anchored** (one number per grain, vs DALI's two-level loop+short-address analog):

- **Unlocked** — fresh deterministic `1..N` in canonical order every solve; nothing committed, so re-stamping is trivially correct.
- **Locked** — each zone pins to its issued DEC #s by slot; a grown/new zone **appends past the high-water** (never refilling a retired gap, so a number never lands on a different box than issued); a shrunk/removed zone drops its numbers as gaps.
- **REVIEW** (surfaced, never silent) — a locked zone whose **interface #** changed keeps its slot numbers but now lives on a different loop. A **decoder-type** change is deliberately *not* flagged (numbers pin by slot; a same-count swap moves no number — the model/BOM delta is TurboDocs/Counts' job).

## Placement (`Shim/Dmx/Services/DmxPlacementService.cs`)

Loop-by-loop click-to-place, mirroring TurboDriver's deploy gesture: prompt one point per loop, drop that loop's decoder + driver instances in a two-column strip, write each decoder's `Switch ID` (`DEC n`), and place auto-syncing tags (SwitchID tag on the decoder, Type-Mark tag on the driver — the driver Type Mark is tagged, not written). Each loop places in its **own transaction** (committed before the next pick — you can't pick inside an open transaction); Escape stops the run and keeps what's placed.

**Non-destructive reconcile.** A re-Place lands only the unbuilt remainder (skips DEC #s already in the model) and removes **orphans** — pairs whose DEC # left the solve — via the persisted registry, deleting the decoder and its paired driver exactly (auto when Unlocked, confirmed when Locked). Manual switch systems and click-placement on survivors are preserved.

**Circuiting (`CircuitZones`).** After placement, a two-phase reconcile creates **one `<unnamed>` (unpaneled) power circuit per Control Zone** — all the zone's DMX fixtures + decoders + drivers — because one zone = one address = one control behaviour = one load (a zone's several decoders are power subdivision under that one address, not separate loads). So the Load Schedule shows one row per zone and TurboZones assigns one Load Name per zone. The reconcile tears down changed/orphaned circuits, then creates, preserving Load Names across a rebuild.

## One-line diagrams (`Shim/Dmx/Services/DmxOneLineService.cs`)

The program **owns one Drafting View per loop** (deterministic name + persisted view id), so a draw is a pure **wipe-and-redraw** from the `DmxOneLineDrawing` snapshot: find/create the owned view, delete only the kinds it draws (see CLAUDE.md "Drafting-view wipe"), then replay symbols (Detail Items), wires (`DetailCurve`s — solid power / dashed control), notes (`TextNote`s), and wire-type markers (Generic Annotations). One transaction, no pick. The designer drops the finished view onto a print sheet by hand; the static WIRE LEGEND is authored once, not drawn here. Missing families/line styles degrade to warnings.

## TurboZones seam (`Shim/Dmx/Services/DmxDemandProvider.cs`)

`IControlSubsystemDemandProvider` — re-solves the persisted design headlessly (`DmxHeadlessSolve`) and reports the `QSE-CI-DMX` interfaces the job needs from **real channel math**, not a hand-picked dropdown. Reports `linkDevices` = interface count and `linkLoads` = total channels (independent budgets: an interface is 1 QS device and its N channels are N switch legs), `DemandMount.LvCompartment`. `QSE-CI-DMX` is a compartment device, so the BOM orders what the designer **placed** and the requirement surfaces in the Panel Breakdown as `(1 of 4 placed)` — the BOM and Panel Schedule can never disagree. A bill *and* a diagnostic is a valid combination (a solve over partially-zoned tape is complete for what it saw and still under-counts) — both are carried. A `Dimming Protocol = DMX` fixture with no `DMX Channels` is invisible to TurboDMX and falls back into Unassigned Circuits rather than vanishing (see Zones README).

## Zone color overlay (`Shim/Dmx/Services/DmxZoneColorService.cs`)

While the window is open, the active view's DMX fixtures are colored by **Control Zone** (a view aid for pooling/grouping, not a deliverable), reverting on close. Close defers so the revert runs on the API thread first; `ModelessWindowGuard` force-close **skips** it (the closing doc's overrides go with it — running the queued revert after the doc is gone crashes Revit). DALI's overlay is a re-scoped copy of this.

## Persistence (`Shim/Dmx/Services/DmxStorageService.cs`)

Doc-singleton ExtensibleStorage on a `DataStorage` element (schema `e5f6a7b8-…`, `TurboSuiteDmxModule`) — the whole `DmxModuleState` (settings, declared loops, clusters, control-system tags, solve snapshot) serialized to **one JSON blob** in `StateJson`, with a parallel `PayloadVersion` int. JSON-backed so payload-shape growth bumps `PayloadVersion`, **not** the GUID (change the GUID only for a true ES field add/remove — see CLAUDE.md). Tolerant read: a corrupt/forward-incompatible payload starts clean rather than crashing the window. Only the `Control Zone` value lives natively (on the tape); this schema references zones by string.

## Layout

- **Pure engine, unit-tested** — `Core/Dmx/`: `DmxSolver` (spine + vocabulary), the packers (`DecoderPacker` / `PowerPacker` / `BreakerPacker` / `InterfacePacker` / `LinkPacker` / `LoopSegmenter`), `Addressing` (channel-count → named Lutron sub-zone decomposition — affects naming only, never watts/budget), `Validation` (`DmxValidator`, the three gates), `Lock/` (Control-Zone-anchored reconciler + numbering pipeline), `Input/` (`DmxModelReader` inputs → contract/zones, `DmxHeadlessSolve` for the seam), `OneLine/` (planner + geometry + drawing snapshot), `Placement/`, `Overlay/DmxZonePalette`, `Persistence/`, `ViewModels/`.
- **Revit-coupled** — `Shim/Dmx/`: `DmxCommand` (modeless entry), `Views/TurboDmxWindow.xaml`, `Services/` (`DmxModelReader` read, `DmxPlacementService` place + circuit, `DmxOneLineService` diagrams, `DmxDemandProvider` the TurboZones seam, `DmxZoneColorService`, `DmxStorageService` + `DmxStatePersister`, `DmxModelSelection`).

## Gotchas

- **Launch needs a clean transaction state** (`doc.IsModifiable` refused) — the window reads the model before opening.
- **Modeless doc-close** force-closes the window and **skips** the zone-color revert (`reverted` short-circuits the deferred-close handler), because the closing document's view overrides are already gone.
- **The three solve gates refuse, they don't split.** An over-cap run or declared loop is the designer's geometry decision; the engine stops with the offending zone/loop named rather than inventing a cable break.
- **DEC #s are system-wide**, assigned by the whole-system solve; per-loop Place only stamps them. Don't renumber per loop.
