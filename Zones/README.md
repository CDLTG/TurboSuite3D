# TurboZones

Two-tab utility for managing circuit load names and visualizing dimmer panel allocation.

## Tab 1 — Load Names

Scans every circuit connected to Lighting or Electrical Fixtures and resolves a load name using:

1. Circuit Comments (highest priority)
2. Fixture Comments (joined, deduplicated)
3. Load Classification full name (fallback)

The resolved label is combined with the room name of the first fixture: `ROOM NAME - label`. Review the proposed updates in the table, then click **Apply** to write all changes in a single transaction.

## Tab 2 — Panel Breakdown

Visualizes how dimmer modules (Relay, 0-10V, ELV) slot into panels for the selected brand.

- **Brands:** Lutron or Crestron (persisted per-document)
- **Panel allocation:** Circuits assigned to panels by name; each panel shows module counts and a compartment slot for Processor, Digital I/O, or DMX
- **Processor links** (Lutron): QS links auto-assigned across processors (99 devices, 512 loads per link)
- **BOM:** Categorized bill-of-materials with part numbers
- **Optimize:** Redistributes circuits across panels to rebalance, with preview before applying

Unassigned circuits (no panel name) are displayed separately.

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
