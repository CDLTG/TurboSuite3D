# TurboNumber

Modeless three-tab MVVM utility for circuit numbers, keypad Switch IDs, and power-supply Switch IDs. Entry `NumberCommand.cs`; Revit-free tab VMs + models in `Core/Number/` (`CircuitNumberTabViewModel` / `KeypadTabViewModel` / `PowerSupplyTabViewModel` over `TabViewModelBase`), Revit ops in `Shim/Number/Services/` behind `INumberRevitOperations` / `ICircuitNumberOperations`. All tabs write via **Apply** through the external event.

## Room Order sidebar (all tabs)

A permanent, window-level sidebar shared by every tab, editing the **project-wide room order** — a shared primitive persisted per-document in `Shared/Services/RoomOrderStorageService.cs` and driven by the shared `Core/Number/ViewModels/RoomOrderViewModel`. It lists **every project room** (all MEP Spaces + Room Regions via `NumberCollectorService.GetAllRoomNames`, including keypad-less and circuit-less rooms), ordered by click-order then drag. While in **Reorder** mode a live name filter narrows the list (filters the view only — click-order and Apply still use the full room list). Both the Keypads grid and the Circuit Numbers **Sort by Room** consume this one order, so they always agree.

## Tab 1 — Circuit Numbers

Panel schedule slot management for the selected panel.

- **Panel selector** with alphabetical ordering
- **Move Up / Move Down** to reposition circuit slots
- **Sort by Room** reorders the panel's circuits into the shared room order — real circuits compact to the top (stable within a room), Spare/Space placeholders clear to Empty, empties sink below — behind a preview/confirm and applied in one undo step. Pure sorter in `Core/Number/Services/RoomOrderPanelSorter.cs` (oracle-tested); the combined clear→regen→swap transaction is `PanelScheduleService.ApplyRoomSort`. **Single-column switchboard panels only** (slot order is read as top-to-bottom).
- **Assign Spare / Assign Space / Remove** for empty slots
- **Open Schedule** to navigate Revit to the panel schedule view
- **Apply** to write panel naming settings (format, prefix, separator)
- Duplicate circuit number detection across all panels (the right-side summary). The summary excludes circuits that are unpaneled or artifacts *by design* — **Feed Through Lugs**, TurboWire **switched** legs (`"switched"` circuit comment), and **DMX/DALI** zone circuits (member fixture running that `Dimming Protocol`) — so a leftover `<unnamed>` there still means a genuinely overlooked circuit. Filter lives in `NumberCollectorService.GetCircuits` / `IsExcludedCircuit`.

## Tab 2 — Keypads

Lists all Lighting Devices whose `TurboSuite Role` type parameter is `Keypad` (see `Core/Shared/Constants/Roles.cs`).

- Editable **Switch ID** column
- **Sort by Room** toggle (**on by default** on first open; the choice persists per-document) sorts the grid by the shared room order and locks column sorting; off restores Value-then-Mark sorting with columns unlocked
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
