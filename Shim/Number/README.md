# TurboNumber

Modeless three-tab MVVM utility for circuit numbers, keypad Switch IDs, and power-supply Switch IDs. Entry `NumberCommand.cs`; Revit-free tab VMs + models in `Core/Number/` (`CircuitNumberTabViewModel` / `KeypadTabViewModel` / `PowerSupplyTabViewModel` over `TabViewModelBase`), Revit ops in `Shim/Number/Services/` behind `INumberRevitOperations` / `ICircuitNumberOperations`. All tabs write via **Apply** through the external event.

## Tab 1 — Circuit Numbers

Panel schedule slot management for the selected panel.

- **Panel selector** with alphabetical ordering
- **Move Up / Move Down** to reposition circuit slots
- **Assign Spare / Assign Space / Remove** for empty slots
- **Open Schedule** to navigate Revit to the panel schedule view
- **Apply** to write panel naming settings (format, prefix, separator)
- Duplicate circuit number detection across all panels

## Tab 2 — Keypads

Lists all Lighting Devices whose family name contains "keypad".

- Editable **Switch ID** column
- **Drag-drop room ordering** sidebar (persisted per-document) controls sort order; while in **Reorder** mode a live name filter narrows the list (filters the view only — click-order and Apply still use the full room list)
- **Auto-number** assigns sequential Switch IDs in sort order
- **Select in Project** selects and reveals the highlighted keypad in the active view

## Tab 3 — Power Supplies

Lists all Lighting Devices with a `Sub-Driver Power` type parameter.

- Editable **Switch ID** with configurable prefix/suffix (default prefix: `X`)
- **Auto-number** assigns IDs in format `{prefix}{number}{suffix}`
- Devices sharing a circuit are sub-lettered top-to-bottom by plan position (model Y), so the suffix matches the column TurboDriver stacks — e.g., X01a, X01b — regardless of grid sort order
- **Select in Project** selects and reveals the highlighted power supply in the active view

## Dependencies

### Required Custom Parameters

**On Lighting Device instances:**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Switch ID` | Text | Editable Switch ID for keypads and power supplies |

**On Lighting Device types:**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Sub-Driver Power` | Double (Watts) | Identifies power supplies (presence of this parameter distinguishes them from keypads) |

**On Panel families (Electrical Equipment):**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Circuit Naming` | ElementId | Naming mode (Prefixed, Standard, Panel Name, By Phase, By Project) |
| `Circuit Prefix` | Text | Custom prefix for circuit numbers |
| `Circuit Prefix Separator` | Text | Separator between prefix and number |

### Family Name Conventions

- **Keypads**: Family name must contain "Keypad" (case-insensitive) — e.g., `AL_Lighting Device_Keypad`
- **Power Supplies**: Identified by presence of `Sub-Driver Power` type parameter

### Other Requirements

- Keypads must be placed in **Rooms** (or over filled regions with Comments) for room-based ordering
- Power supplies must be on **electrical circuits** for circuit number display
- At least one **panel** (`OST_ElectricalEquipment`) for the Circuit Numbers tab
