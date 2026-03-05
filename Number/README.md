# TurboNumber

Three-tab utility for managing circuit numbers, keypad Switch IDs, and power supply Switch IDs.

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
- **Drag-drop room ordering** sidebar (persisted per-document) controls sort order
- **Auto-number** assigns sequential Switch IDs in sort order

## Tab 3 — Power Supplies

Lists all Lighting Devices with a `Sub-Driver Power` type parameter.

- Editable **Switch ID** with configurable prefix/suffix (default prefix: `X`)
- **Auto-number** assigns IDs in format `{prefix}{number}{suffix}`
- Devices sharing a circuit are sub-lettered (e.g., X01a, X01b)

All tabs support **Apply** to write changes to Revit.
