# TurboZones

Modeless utility: circuit load names, shade load names, and dimmer-panel allocation. The window stays open while you work in Revit — pan, zoom, select, and run other commands without closing it. All Revit writes go through an `IExternalEventHandler` (see CLAUDE.md "Modeless pattern"). DALI loop *declaration* used to be a third tab here; it now lives in the standalone **TurboDALI** command, and TurboZones is a pure consumer of the persisted DALI state (placement + demand only). A **Shade Names** tab appears between Load Names and Panel Breakdown **only on jobs that have shade circuits**, so non-shade jobs keep the two-tab layout.

## Tab — Load Names

Scans every circuit connected to Lighting or Electrical Fixtures and resolves a load name using:

1. Circuit Comments (highest priority)
2. Fixture Comments (joined, deduplicated)
3. Load Classification full name (fallback)

The resolved label is combined with the room name of the first fixture: `ROOM NAME - label`. A per-circuit **Room Override** column lets you substitute a different room name for a single circuit; overrides are persisted in ExtensibleStorage (keyed by circuit) so they survive reopening the window, and apply only to the circuit they were set on. Review the proposed updates in the table, then click **Apply Load Names** to write all changes in a single transaction. Click any row to mark it active (blue left-edge stripe), then click **Select in Project** to highlight and zoom to that circuit in Revit's active view without closing the window.

## Tab — Shade Names

The **same grid and machinery as Load Names**, fed shade circuits instead of lighting ones (`ShadeCircuitCollectorService`, the mirror of the lighting collector's shade drop). A shade circuit is one shade motor by convention — **circuit = output** — so each row is one QSPS-10PNL output, named exactly like lighting: `ROOM NAME - comment`, room from owned Spaces (region fallback in 2D, override as the escape hatch), comment typically supplied at wire-time by TurboWire. `LoadNameTabViewModel` is reused verbatim (only the header and the fed circuit list differ); **Apply Shade Names** writes `RBS_ELEC_CIRCUIT_NAME` back per circuit. The dimming/load-classification columns are dropped — a QS motor has none.

**Separate override store.** Shade room overrides live in their **own** ExtensibleStorage schema (`ShadeRoomOverrideStorageService`, a distinct GUID) rather than the lighting store. This is load-bearing: a Load-Names "Apply" does a full-overwrite `Write` built from *its* circuit snapshot, pruning any key it didn't enumerate — so a single shared store would have each tab's Apply prune the other's overrides. The lighting `RoomOverrideStorageService` and the shade one share one instance core (`RoomOverrideStore`, injected GUID/name); TurboWire routes a shade circuit's wire-time override to the shade store by its `shadePanels` mode. (New schema, so no migration — nothing stale to clear.)

## Tab — Panel Breakdown

Visualizes how dimmer modules (Relay, 0-10V, ELV) slot into panels for the selected brand.

- **Brands:** Lutron or Crestron (persisted per-document)
- **Lutron relay module (default):** Relay loads share the `LQSE-4T5-120-D` 0-10V/switching module with 0-10V loads. Toggle "Dedicated relay module (LQSE-4S8)" in the top bar to allocate the switching-only `LQSE-4S8-120-D` for Relay loads instead.
- **Pack RELAY + 0-10V together** (Lutron, non-dedicated only): by default Relay and 0-10V loads split onto separate `LQSE-4T5` modules even though they share the part, which can leave half-empty spares. Enable this toggle to pack both into one pool — pure-relay modules, a single mixed *seam* module at the boundary (labeled `RELAY / 0-10V`), then pure-0-10V — reclaiming the spares. Greyed out when *Dedicated relay module* is on (the `LQSE-4S8` is a physically distinct module the two can't share); the allocator enforces the same precondition by part-number equality (`PanelAllocationService.BuildPanelBreakdown`), so the flag is a silent no-op for the dedicated module and for Crestron. Under amp pressure the FFD fallback may produce more than one mixed module, re-sorted relay-first so the panel still reads as a gradient. The mixed tile derives its label from its slot protocols (`ModuleResult.TypeLabel`), not the `0-10V` sort key.
- **Panel allocation:** Circuits grouped by zone (ZONE N panels); recommends minimum panels per zone and distributes modules across them. Each panel supports a compartment slot for Processor, Digital I/O, or DMX. LV21 panels (dual-compartment, no modules) are supported.
- **Control subsystems:** A subsystem that solves its own hardware reports it through `IControlSubsystemDemandProvider` (`Core/Zones/Services/`), and the BOM plus the QS-link roll-up consume that count rather than re-deriving one. **TurboDMX** is the first: `DmxDemandProvider` re-solves the persisted DMX design headlessly and reports the `QSE-CI-DMX` interfaces the job needs from real channel math, plus the QS device and switch-leg budgets they consume (1 device and 0 zones per interface; 1 leg per DMX channel).
- **A subsystem states a requirement; placement still decides the order.** `QSE-CI-DMX` is a compartment device, so its BOM quantity follows the dropdown exactly as the processor's does, and the solve becomes the `(1 of 4 placed)` annotation telling the designer to go site the rest. That is always actionable: overriding a panel to **LV21** frees two compartments, and the allocator adds a panel to re-home the displaced modules. A part with no compartment (the DALI DIN module) has no compartment placement to defer to and is ordered at its solved quantity.
- **Shades (Sivoia QS)** are the second subsystem, treated like lighting: **a recommendation off the circuits, not placed hardware.** `ShadeDemandProvider` (`Shim/Zones/Services/`) finds shade circuits — a circuit is a shade circuit when a connected fixture's family name contains **"Shade Motor"** (catching the 3D `AL_Electrical Fixture_Shade Motor` and the 2D `Shade Motor`) — groups them by **location** (the circuit's panel name, e.g. SHADE 1 / SHADE 2), and `Core/Zones/Services/ShadeSolver.cs` recommends `ceil(shades / 10)` **QSPS-10PNL per location, summed** (33 in SHADE 1 + 4 in SHADE 2 → 4 + 1 = five panels). The order is `DemandMount.External` (ordered, competing for no compartment) and prints under its own **Shades** BOM section, apart from Accessories. **Link accounting:** a shade is 1 QS device + 1 switch leg and each recommended panel is 1 more device (a full panel → 11 devices / 10 legs), riding the same link budget as the lighting; the QSPS-10PNL's Link terminals carry no V+, so shades draw **0 PDU** and never touch the QSPS-DH supply — the panel powers its own outputs from mains. Shade circuits are dropped from the lighting collector (`ZonesCollectorService`, same shade-motor test) so their Electrical-Fixture motors don't become spurious lighting zones.
- **Shades in the visualizer.** The recommended QSPS-10PNL are also **drawn** in the Panel Breakdown, per location. Shade locations share the lighting **Location N** space — `SHADE 1` parses to the same number as `ZONE 1` (`PanelAllocationService.ParseLocationNumber` handles both `ZONE N` and `SHADE N`), so a location's shade panels merge in after its lighting panels, continuing the letter run (…`1-C` lighting, then `1-D`, `1-E` shades). A shade number with no matching lighting spawns its **own** (pure-shade) location column with zero lighting panels. A shade with no `SHADE N` panel is **not drawn** — it is neither counted nor given a column; it surfaces as a **shade BOM warning** (`ShadeSolver` emits a diagnostic, "*N shade motors not assigned to a SHADE panel*"), the same way unassigned loads are handled everywhere else. Each shade panel is an **LV21-sized, pastel-gray, read-only** card (no size/compartment controls — a shade panel is a fixed ten-output device) whose footer shows the **shade fill** (`1-D  10/10`) instead of a module count. Shade panels get their **own columns** beside the lighting and stack **among themselves in bottom-aligned groups of three** (`LocationResult.ShadeColumns`), mimicking the field — they never share a column with a dimmer panel. The tiles come from `ShadeSolver.PanelFills`, the **same** per-location count the BOM sums (`PanelsForLocation`), so the panels drawn always total the panels ordered. They stay out of the location's module/overcapacity math (their own `LocationResult.ShadePanels`).
- **DALI (Lutron LQSE2-1DALUNV-D)** is the third subsystem, and the first whose module lands in a **dimming-panel DIN slot** (`DemandMount.DinSlot`), not a compartment. Its grain is **loops**, not circuits: the designer declares loops (named groupings of `Control Zone` values, persisted in `DaliStorageService`), and `DaliDemandProvider` (`Shim/Dali/Services/`) counts DALI **addresses** per loop — `Dimming Protocol = DALI`, and **one address = one DALI circuit**, not one fixture (so several tape runs on one shared-driver circuit collapse to a single load/leg; the driver/decoder that shares that circuit is a lighting *device*, not a fixture, so it is ignored). The pure `Core/Dali/Input/DaliLoadCounter.cs` does the collapse; an uncircuited DALI fixture counts one-each until wired. Per-loop tallies go to `Core/Dali/DaliSolver.cs`. The solver gives **module count = loop count** (one DALI bus per module), `LinkDevices` = module count (the module is the only QS device; its loads ride the DALI bus downstream), `LinkLoads` = total loads, PDU 0; over **64 loads/bus** is a warning, not an auto-split. The module is **ordered job-wide** from that demand (single BOM/link authority). **Placement** into a specific ZONE N panel is the designer's required per-loop zone assignment (declared in **TurboDALI**, read once at window open from the persisted loops via `Core/Dali/Input/DaliPlacementMapper.cs`), fed to `PanelAllocationService` as a `daliModulesByZone` map; a placed DALI slot occupies a panel module-slot (counting toward the panel-count recommendation) but is tagged `ModuleResult.OrderedBySubsystem` so it is **excluded from the BOM roll-up and the link budget** — otherwise the job-wide demand and the placed slot would double-count. DALI circuits are created **unassigned** (like DMX) and, now that DALI resolves `HandledBySubsystem` in `DimmingModuleResolver`, are silenced from Unassigned exactly when the subsystem accounts for them.
- **DMX warnings never fail a document.** A design that will not solve, or one solved over partly zoned tape, contributes a warning line naming the reason — including *"N DMX fixtures have no Control Zone assigned"*.
- **A subsystem only earns silence by speaking.** DMX circuits are excluded from Unassigned Circuits because TurboDMX owns them — but only when it actually accounted for them, with parts or with a reason. A circuit whose fixtures declare `Dimming Protocol = DMX` yet carry no `DMX Channels` is invisible to TurboDMX, so it falls back into Unassigned Circuits instead of disappearing from every surface at once.
- **Panel size overrides:** Users can force any panel to a different size; modules auto-redistribute to accommodate.
- **Processor links** (Lutron): two per placed processor, packed by `Core/Zones/Services/ControlLinkPacker.cs`. See "Control links" below — this is a recommendation surface, and there is deliberately no way to assign a panel to a link.
- **Amp-aware allocation** (Lutron): Module limits enforced per part number — ELV `LQSE-4A5` 6.6/4.2/16 A (slot 1 / slots 2-4 / module total), 0-10V `LQSE-4T5` 5.0/5.0/20 A, switching `LQSE-4S8` 8.0/8.0/16 A. Circuits over the slot-2-4 limit auto-promote to slot 1. When sequential circuit-number order produces an overloaded module, the allocator falls back to first-fit-decreasing bin-packing only when it would reduce module count or overload count. Overloaded modules render with a red background in Panel Breakdown and overloaded rows render in bold red on a pale red highlight in the Panel Schedule PDF.
- **BOM:** Categorized bill-of-materials with part numbers, built by `Core/Zones/Services/ControlBomBuilder.cs` — the **same builder** the TurboDocs Control BOM PDF uses, so the two cannot disagree about what to order. The only per-consumer difference is `BomAudience`, which governs presentation and never quantities: this tab renders as `DesignSurface`, keeping zero-quantity lines and annotating a shortfall.
- **Processor count follows what is placed**, not what is recommended — over *or* under. A processor's location can't be derived; it is an assignment the designer makes to a specific panel, so this tab is the single source of truth for the count. The recommendation stays advisory: placing fewer than recommended flags the BOM line with `(N of M placed)` rather than silently inflating the order. Processors are counted **per compartment slot**, so an LV21 with a processor in each of its two compartments is two processors — two sidebar blocks, four link bars, and two supplies.
- **Power supply** (Lutron `QSPS-DH-1-75-H`) is sized from the QS-link **power-draw budget (PDU)**, not one-per-processor: each supply gives +75, the processor draws −8 on its first QS link and each device its own draw (keypad −1, QSE-IO −3, QSE-CI-DMX −2; modules and panels 0), and the order is `ceil(|net| / 75)` summed over the QS links, plus one per all-wireless processor. The signed draws ride the same packer that fills the bars (`Core/Zones/Services/ControlLinkPacker.cs` → `PackedLink.ConsumedPdu`); the −8 and the sizing live in `ControlBomBuilder`. Feasibility is a **global** check — total supplies needed vs the supply positions the placed panels provide (LV21→2, PD4→1, PD8→1, rest→0) — so a design-surface warning names a shortfall when the panel mix cannot hold the count.
- **Unassigned circuits:** Circuits without a recognized zone panel name are flagged. Switch-wired circuits are excluded from this warning.

## Control links

The sidebar answers exactly one question: **do I need another processor?** The Panel Breakdown is a
recommendation surface — the designer makes two decisions (panel sizes, and where processors, I/O and
DMX interfaces go) and everything else is derived. There is **no manual panel→link assignment and
will not be**: if the derived layout does not fit, the answer is another processor, and the bars are
how that is said. Downstream the design is imported into Lutron's own software, which is what the
BOM's "Verify bill of materials with official control system documentation" is for.

**One packer, two questions.** `ControlLinkPacker` both recommends a processor count (pack into
unlimited links, divide by two) and fills the capacity bars (pack into the links that exist, show
overflow). They were separate algorithms until they were merged, and they disagreed — the BOM pooled
every device in the job and divided, which assumes a panel's modules can be split across two links.
They cannot. The invariant now holds by construction: **if a bar is over capacity the BOM recommends
more processors, and if it recommends more, some bar is over.**

**A link has three budgets, and they are different kinds of thing** (Lutron 3691127f p.2):

| Budget | QS link | Clear Connect Type A |
|--------|---------|----------------------|
| Devices | 99 | 99 |
| Switch legs | 512 | 100 |
| Hybrid Repeaters | — | 4 |

So a Clear Connect link shows **three** bars where QS shows two. The repeater cap is a cap on one
*kind* of device, not on devices — a wireless keypad consumes the 99 exactly as a wired one consumes a
QS link's, and can run well past four. Reading the two as one number makes every wireless device past
the fourth look like an overflow on a link with 95 slots free.

**A switch leg is the smallest controllable output** — *"dimmed or switched circuits, HomeWorks
Digital or DALI addressable devices (ballasts, drivers, and interfaces), a single DMX channel, contact
closure outputs, and Sivoia QS shade drives."* Devices are counted at **nameplate**: a four-output
module holding one circuit still presents four legs, and a QSE-IO presents its **five** flexible I/O
terminals as five legs (`BrandConfig.DeviceSwitchLegs`), whatever a job configures. **Sivoia QS shade
drives** are counted through the shades subsystem above (one leg + one device each, via the placed
QSPS-10PNL panels) rather than this nameplate table — a shade's leg rides its circuit, not a fixed
per-device count.

**Wireless takes whole links.** One repeater converts a link to Clear Connect, so a job that would run
on one processor can need two purely to carry wireless — five repeaters is two CC-A links, which is a
processor. Wireless devices ride those links and consume their device budget; wired keypads never do.
When the link budget is fixed, CC-A stops one link short of consuming every link that has QS work, so
the overflow shows as an over-capacity bar rather than hiding the panels.

**Packing is first-fit decreasing over indivisible units.** A panel is one unit — its modules plus any
compartment device sited in it all ride the same link. Interfaces a subsystem requires but nobody has
sited yet float and pack anywhere; keypads are one device each and pour into whatever room is left.

## Dependencies

### Required Custom Parameters

**On Lighting/Electrical Fixture types:**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Dimming Protocol` | Text | Drives module assignment, via the protocol→module map in `Core/Zones/Services/DimmingModuleResolver.cs` |

Module type is resolved from the fixtures' **Dimming Protocol**, not the connector-level `Load Classification Abbreviation` this used to read. That value lived on a connector inside each family, printed on nothing, and was easy to leave unset — and a blank silently dropped the circuit out of allocation. Dimming Protocol carries the same information, prints on the fixture schedule (so it gets proofread), and already drives TurboDriver.

Protocols fall into three categories:

| Protocol | Behavior |
|----------|----------|
| `ELV`, `0-10V`, `MLV`, `RELAY` | Allocates. Note **MLV → ELV module** — the mapping is not the identity |
| `WIFI` | Network-controlled, rides no module. Excluded **silently**, like a switch-wired circuit |
| `DMX` | Rides no DIN module — the `QSE-CI-DMX` is a QS-link interface in the LV compartment, and **TurboDMX** counts them. Excluded **silently, but only while TurboDMX accounts for it** (reports parts *or* a reason). If TurboDMX saw no matching fixtures at all, nothing is counting the circuit and it surfaces in **Unassigned Circuits** rather than vanishing |
| `DALI` | Subsystem-owned (`LQSE2-1DALUNV-D` DIN module), like DMX. Excluded **silently while the DALI subsystem accounts for it**; DALI fixtures modeled with no loops declared surface the *"declare loops in TurboZones"* warning instead of vanishing |
| blank / unrecognized | Authoring gap → **Unassigned Circuits** |

A circuit whose fixtures declare more than one protocol resolves to one module type (first in sorted order, so it does not depend on Revit's element enumeration order).

**On Keypad families (Lighting Devices) and Hybrid Repeaters (Electrical Fixtures):**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Two Gang` | Yes/No (Integer) | **Link math only.** A two-gang keypad is two addressed devices in one backbox, so it counts twice against a link's 99. It does *not* split the BOM — see below |
| `Wireless` | Yes/No (Integer) | Rides a Clear Connect link instead of a QS link, consuming that link's device budget. Read type-first with instance override, like `Two Gang`. **Absent reads as wired**, which is the behaviour that shipped before it existed |
| `Catalog Number1`–`6` | Text | The parts to order. One device is often several: base unit + button kits + faceplate |
| `Catalog Qty1`–`6` | Text | How many of that slot per device — blank ⇒ 1 each, `N` ⇒ N each, `1/N` ⇒ 1 per N, `N @type` ⇒ N per type |
| `Description`, `Description2` | Text | Words for `Catalog Number1` and `Catalog Number2` respectively |

**A type answers two independent questions, and neither derives from the other.** How many *devices*
it occupies on a link (`Two Gang`, `Wireless`, instance count) and what it *costs to buy* (the catalog
slots). "Button kit qty 2" is not "2 gangs"; "2 gangs" is not "2 of everything". So the BOM groups
purely by catalog number — a two-gang keypad is a different model with its own number, and the lines
separate on their own — while the link math never sums order rows to get a device count.

The quantity grammar is `CatalogQtyParser` / `CatalogQtyRule.Evaluate`, borrowed from the Counts
module; `Core/Zones/Services/CatalogSlotTally.cs` is the only other consumer and takes **nothing else**
from Counts, where Lutron control devices are not declared at all. `N @ft` / `N @in` are rejected here:
stock-cut divides Linear Length, which a control device does not have. An unparseable token falls back
to one-per-device and is flagged in this tab with the type and slot named — never dropped, because a
part nobody can parse is still a part somebody has to buy.

A type with no catalog number is still ordered, with the part column falling back to the generic word
(`Keypad`, `Hybrid Repeater`) and an amber flag here. Descriptions pair with slots by position and
stop at two, because a family carries two description fields and six catalog slots. Slots 3–6 print
without words; nothing in the library uses them yet.

**On Panel families (Electrical Equipment):**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Catalog Number1` | Text | Panel part number for brand-specific lookups |

## DALI — consumed here, declared in TurboDALI

DALI loop *declaration* (grouping Control Zones into loops, assigning each a ZONE) moved out to the standalone **TurboDALI** command (`Shim/Dali/`). TurboZones no longer edits DALI — it **reads** the persisted loops (`DaliStorageService`) once at window open and consumes them two ways: the placement map (`DaliPlacementMapper`) drives the Panel Breakdown's DALI slots, and `DaliDemandProvider` supplies the job-wide module/link demand. In the Panel Breakdown a placed DALI slot renders pastel purple and shows **loads / 64** (red when over one bus). To change a loop or its ZONE, open TurboDALI; the edits reflect on the next TurboZones open.

### Built-In Parameters Used

| Parameter | On | Access |
|-----------|----|--------|
| `RBS_ELEC_CIRCUIT_NAME` | Circuits | Read/Write — load name updated to `ROOM NAME - label` |
| `RBS_ELEC_CIRCUIT_NUMBER` | Circuits | Read |
| `RBS_ELEC_CIRCUIT_PANEL_PARAM` | Circuits | Read — panel assignment |
| `ALL_MODEL_INSTANCE_COMMENTS` | Circuits | Read/Write — circuit comments |

### Other Requirements

- Circuits must be connected to **Lighting Fixtures** or **Electrical Fixtures**
- Fixtures should have resolvable **room names** (from host room or filled region Comments)
- Panel Breakdown tab assumes **Lutron** or **Crestron** brand configurations
