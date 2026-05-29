# TurboZones

Two-tab modeless utility for managing circuit load names and visualizing dimmer panel allocation. The window stays open while you work in Revit — pan, zoom, select, and run other commands without closing it. All Revit writes go through an `IExternalEventHandler` (see CLAUDE.md "Modeless pattern").

## Tab 1 — Load Names

Scans every circuit connected to Lighting or Electrical Fixtures and resolves a load name using:

1. Circuit Comments (highest priority)
2. Fixture Comments (joined, deduplicated)
3. Load Classification full name (fallback)

The resolved label is combined with the room name of the first fixture: `ROOM NAME - label`. Review the proposed updates in the table, then click **Apply Load Names** to write all changes in a single transaction. Click any row to mark it active (blue left-edge stripe), then click **Select in Project** to highlight and zoom to that circuit in Revit's active view without closing the window.

## Tab 2 — Panel Breakdown

Visualizes how dimmer modules (Relay, 0-10V, ELV) slot into panels for the selected brand.

- **Brands:** Lutron or Crestron (persisted per-document)
- **Lutron relay module (default):** Relay loads share the `LQSE-4T5-120-D` 0-10V/switching module with 0-10V loads. Toggle "Dedicated relay module (LQSE-4S8)" in the top bar to allocate the switching-only `LQSE-4S8-120-D` for Relay loads instead.
- **Panel allocation:** Circuits grouped by zone (ZONE N panels); recommends minimum panels per zone and distributes modules across them. Each panel supports a compartment slot for Processor, Digital I/O, or DMX. LV21 panels (dual-compartment, no modules) are supported.
- **Panel size overrides:** Users can force any panel to a different size; modules auto-redistribute to accommodate.
- **Processor links** (Lutron): QS links auto-assigned across processors (99 devices, 512 loads per link). Clear Connect Type A links reserved for hybrid repeaters when present.
- **Amp-aware allocation** (Lutron): Module limits enforced per part number — ELV `LQSE-4A5` 6.6/4.2/16 A (slot 1 / slots 2-4 / module total), 0-10V `LQSE-4T5` 5.0/5.0/20 A, switching `LQSE-4S8` 8.0/8.0/16 A. Circuits over the slot-2-4 limit auto-promote to slot 1. When sequential circuit-number order produces an overloaded module, the allocator falls back to first-fit-decreasing bin-packing only when it would reduce module count or overload count. Overloaded modules render with a red background in Panel Breakdown and overloaded rows render in bold red on a pale red highlight in the Panel Schedule PDF.
- **BOM:** Categorized bill-of-materials with part numbers. Modules collapse to one line per resolved part number, so a single module type carrying multiple dimming roles (e.g. LQSE-4T5 for both 0-10V and Relay) appears as one combined quantity.
- **Unassigned circuits:** Circuits without a recognized zone panel name are flagged. Switch-wired circuits are excluded from this warning.

## Dependencies

### Required Custom Parameters

**On Electrical Circuits:**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Load Classification Abbreviation` | Text | Dimming type identifier (ELV, 0-10V, Relay) — drives module assignment |

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
