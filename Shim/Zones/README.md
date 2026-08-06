# TurboZones

Two-tab modeless utility for managing circuit load names and visualizing dimmer panel allocation. The window stays open while you work in Revit — pan, zoom, select, and run other commands without closing it. All Revit writes go through an `IExternalEventHandler` (see CLAUDE.md "Modeless pattern").

## Tab 1 — Load Names

Scans every circuit connected to Lighting or Electrical Fixtures and resolves a load name using:

1. Circuit Comments (highest priority)
2. Fixture Comments (joined, deduplicated)
3. Load Classification full name (fallback)

The resolved label is combined with the room name of the first fixture: `ROOM NAME - label`. A per-circuit **Room Override** column lets you substitute a different room name for a single circuit; overrides are persisted in ExtensibleStorage (keyed by circuit) so they survive reopening the window, and apply only to the circuit they were set on. Review the proposed updates in the table, then click **Apply Load Names** to write all changes in a single transaction. Click any row to mark it active (blue left-edge stripe), then click **Select in Project** to highlight and zoom to that circuit in Revit's active view without closing the window.

## Tab 2 — Panel Breakdown

Visualizes how dimmer modules (Relay, 0-10V, ELV) slot into panels for the selected brand.

- **Brands:** Lutron or Crestron (persisted per-document)
- **Lutron relay module (default):** Relay loads share the `LQSE-4T5-120-D` 0-10V/switching module with 0-10V loads. Toggle "Dedicated relay module (LQSE-4S8)" in the top bar to allocate the switching-only `LQSE-4S8-120-D` for Relay loads instead.
- **Panel allocation:** Circuits grouped by zone (ZONE N panels); recommends minimum panels per zone and distributes modules across them. Each panel supports a compartment slot for Processor, Digital I/O, or DMX. LV21 panels (dual-compartment, no modules) are supported. A compartment holding a subsystem device is captioned with what it serves — a DMX slot lists its control zones.
- **Control subsystems:** A subsystem that solves its own hardware reports it through `IControlSubsystemDemandProvider` (`Core/Zones/Services/`), and the BOM plus the QS-link roll-up consume that count rather than re-deriving one. **TurboDMX** is the first: `DmxDemandProvider` re-solves the persisted DMX design headlessly and reports `QSE-CI-DMX` interfaces from real channel math, plus the QS device and switch-leg budgets they consume (1 device and 0 zones per interface; 1 leg per DMX channel). Unlike the processor, **quantity here follows the subsystem, not what was placed** — a panel holds one special device, so placement cannot express "four interfaces"; the dropdown states *where*, the solve states *how many*. Placing fewer than solved flags the BOM line with `(N of M placed)`. A DMX design that will not solve never fails the BOM: it contributes a warning line naming the reason.
- **Panel size overrides:** Users can force any panel to a different size; modules auto-redistribute to accommodate.
- **Processor links** (Lutron): QS links auto-assigned across processors (99 devices, 512 loads per link). Clear Connect Type A links reserved for hybrid repeaters when present.
- **Amp-aware allocation** (Lutron): Module limits enforced per part number — ELV `LQSE-4A5` 6.6/4.2/16 A (slot 1 / slots 2-4 / module total), 0-10V `LQSE-4T5` 5.0/5.0/20 A, switching `LQSE-4S8` 8.0/8.0/16 A. Circuits over the slot-2-4 limit auto-promote to slot 1. When sequential circuit-number order produces an overloaded module, the allocator falls back to first-fit-decreasing bin-packing only when it would reduce module count or overload count. Overloaded modules render with a red background in Panel Breakdown and overloaded rows render in bold red on a pale red highlight in the Panel Schedule PDF.
- **BOM:** Categorized bill-of-materials with part numbers, built by `Core/Zones/Services/ControlBomBuilder.cs` — the **same builder** the TurboDocs Control BOM PDF uses, so the two cannot disagree about what to order. The only per-consumer difference is `BomAudience`, which governs presentation and never quantities: this tab renders as `DesignSurface`, keeping zero-quantity lines and annotating a shortfall.
- **Processor count follows what is placed**, not what is recommended — over *or* under. A processor's location can't be derived; it is an assignment the designer makes to a specific panel, so this tab is the single source of truth for the count. The recommendation stays advisory: placing fewer than recommended flags the BOM line with `(N of M placed)` rather than silently inflating the order. The power supply follows the same rule (one per placed processor).
- **Unassigned circuits:** Circuits without a recognized zone panel name are flagged. Switch-wired circuits are excluded from this warning.

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
| `DMX` | Rides no DIN module — the `QSE-CI-DMX` is a QS-link interface in the LV compartment, and **TurboDMX** counts them. Excluded **silently**; the hardware is ordered from subsystem demand, not from this map |
| `DALI` | A real module TurboSuite does not allocate yet → **Unassigned Circuits** |
| blank / unrecognized | Authoring gap → **Unassigned Circuits** |

A circuit whose fixtures declare more than one protocol resolves to one module type (first in sorted order, so it does not depend on Revit's element enumeration order).

**On Keypad families (Lighting Devices):**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Two Gang` | Yes/No (Integer) | Identifies two-gang keypad configurations |

**On Panel families (Electrical Equipment):**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Catalog Number1` | Text | Panel part number for brand-specific lookups |

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
