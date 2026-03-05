# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TurboSuite is a unified Autodesk Revit 2025 add-in for electrical/lighting automation, written in C#. It consolidates seven commands (TurboDriver, TurboBubble, TurboTag, TurboWire, TurboZones, TurboNumber, TurboCompact) into a single `TurboSuite.dll` targeting .NET 8.0-windows. The add-in implements `IExternalApplication` to register a ribbon panel with seven `IExternalCommand` buttons.

## Build Commands

```bash
dotnet build TurboSuite.csproj
```

Platform target is **x64**. There are no automated tests or linting configurations.

**IMPORTANT**: Always use Windows-style paths for `dotnet`/MSBuild commands (e.g., `'C:\Users\jacobq\...\TurboSuite.csproj'`). Never use WSL-style `/mnt/c/...` paths — they cause `MSB1001` errors.

## Git Repository

- **Remote:** GitHub (CDLTG/TurboSuite3D), default branch `main`
- **Ignored:** `Specs/` (local-only reference docs), `bin/`, `obj/`, `.vs/`, `.idea/`
- Do NOT commit files from `Specs/` — they are historical reference documents kept locally only.
- Always commit and push `.gitignore` changes so they take effect on GitHub.

## Deployment

The post-build target copies `TurboSuite.addin` and `TurboSuite.dll`/`.pdb` to:
```
%APPDATA%\Autodesk\Revit\Addins\2025\
%APPDATA%\Autodesk\Revit\Addins\2025\TurboSuite\
```
Revit auto-discovers `.addin` files from that directory on startup.

## Workflow Rules

### 3D and 2D Drafting Support
**IMPORTANT**: All TurboSuite commands MUST work in both workflows:
- **3D Model**: Hosted families (ceiling, wall, floor, line-based) with 3D geometry in plan/RCP views.
- **2D Drafting**: Unhosted Lighting Fixture and Electrical Fixture families with no 3D geometry, placed in Floor Plan views over linked 2D CAD files. These families still have electrical connectors, circuits, wires, tags, and the same parameters as 3D families. The only difference is no hosting and no Room elements.

When implementing new features or modifying existing commands:
1. Ensure code handles `fixture.Host == null` and `fixture.HostFace == null` (unhosted families).
2. `GeometryHelper.IsOnVerticalFace()` returns false and `IsLineBasedFixture()` returns false for unhosted families — existing branching handles this naturally.
3. Room resolution (`LinkedRoomFinderService.FindRoom`) returns null in 2D projects — callers already handle null gracefully; users enter room names manually.
4. Do NOT assume fixtures have a host, a host face normal, or a LocationCurve.

### Fixture Transform and Direction Offsets
When converting fixture-local offsets to global coordinates (e.g., tag placement directions):
- **Use `BasisX` rotation angle only** — do NOT use the full `fixture.GetTransform()` with `BasisX * localX + BasisY * localY + BasisZ * localZ`. The `BasisY` and `BasisZ` vectors are inverted for ceiling-hosted fixtures (BasisZ points downward, BasisY points in -Y), which causes Up/Down directions to flip depending on hosting type.
- **Pattern**: Extract rotation via `Math.Atan2(transform.BasisX.Y, transform.BasisX.X)`, then apply a 2D rotation to the local offset. This gives consistent results for ceiling-hosted, floor-hosted, and unhosted fixtures.
- **RCP views do NOT reflect the X axis**. An RCP is a horizontal mirror (Z-reflection only). Model +X appears to the RIGHT on screen in both floor plans and RCPs. Do NOT negate the X component for RCP views.
- See `TagPlacementService.TransformToGlobal` for the reference implementation.

### Explain Before Acting
When asked to explain something, provide the explanation only. Do not assume a code change, behavior modification, or memory update is wanted unless explicitly requested.

### Revit API Parameter Safety
**IMPORTANT**: Before implementing anything that reads or writes a Revit parameter:
1. Verify the parameter is writable via the Revit API (some are read-only or computed).
2. List any known restrictions, limitations, or alternative approaches.
3. For `ElementId` storage type parameters, probe valid `ElementId` values rather than assuming string or integer assignment will work.
4. Only proceed to implementation after confirming feasibility.

### Specification Documents
Versioned spec `.txt` files are in `Specs/`. These are historical reference documents only — do NOT use them to influence decisions or implementation unless the user explicitly asks to reference a spec.

## Architecture

### Entry Point

`TurboSuite.App.TurboSuiteApplication` (IExternalApplication) registers a "TurboSuite" ribbon panel with seven buttons, each pointing to a command class.

### Namespace / Folder Structure

| Namespace | Purpose |
|-----------|---------|
| `TurboSuite.App` | Entry point (`TurboSuiteApplication`) |
| `TurboSuite.Shared.Helpers` | `GeometryHelper`, `ParameterHelper`, `CalculationHelper` |
| `TurboSuite.Shared.Filters` | `LightingFixtureSelectionFilter` (accepts both Lighting + Electrical Fixtures), `LightingFixtureTagFilter` |
| `TurboSuite.Shared.Models` | `WallLocalCoordinateSystem` |
| `TurboSuite.Driver` | `DriverCommand` + Services, Models, ViewModels, Views (MVVM) |
| `TurboSuite.Bubble` | `BubbleCommand` + Placement calculators, Services, Constants, Filters |
| `TurboSuite.Tag` | `TagCommand` + Services, Helpers, Constants |
| `TurboSuite.Wire` | `WireCommand` + Services, Helpers, Constants |
| `TurboSuite.Zones` | `ZonesCommand` + Services, Models, ViewModels, Views (MVVM) |
| `TurboSuite.Number` | `NumberCommand` + Services, Models, ViewModels, Views (MVVM) |
| `TurboSuite.Compact` | `CompactCommand` — family document cleanup (no UI beyond TaskDialog) |

### Command Modules

- **Driver** (MVVM) — Manages lighting device family types for circuits with "Remote Power Supply" enabled. `DriverSelectionService` recommends driver types by matching fixture wattage, manufacturer, dimming protocol, and voltage. Uses First-Fit Decreasing bin-packing with recursive fixture splitting. Opens `TurboDriverWindow`.
- **Bubble** — Creates switchleg tags and wires for lighting fixtures and electrical fixtures. Lighting Fixtures are selected via their tags and use strategy pattern with `IPlacementCalculator` (Horizontal, LineBased, VerticalFace). Electrical Fixtures are selected directly; default path places tag left/right along localX, while vertical families (Exhaust, Fireplace Igniter) place tag up/down along localY with arc-approximated wire vertices. Uses `BubbleSelectionFilter` to accept both element types.
- **Tag** — Auto-places lighting fixture type tags. Handles point-based, line-based, and face-based fixtures.
- **Wire** — Creates wire connections between lighting fixtures and electrical fixtures with arc routing. Wall sconces and receptacles use spline routing with wall-normal offsets (sconces 2.5", receptacles 3"). Fixtures are grouped by category — no mixing of Lighting Fixtures and Electrical Fixtures in a single wire chain.
- **Zones** (MVVM) — Two-tab `TurboZonesWindow`: Tab 1 (Load Names) manages circuit load names with room/label resolution; Tab 2 (Panel Breakdown) visualizes dimmer module/panel allocation with brand configs (Lutron, Crestron).
- **Number** (MVVM) — Three-tab `TurboNumberWindow`: Tab 1 (Circuit Numbers) manages panel schedule slot layouts via `PanelScheduleView`; Tab 2 (Keypads) edits Switch ID on keypad devices with drag-drop room ordering; Tab 3 (Power Supplies) edits Switch ID on devices with "Sub-Driver Power" parameter. All tabs support auto-numbering with cascading sequential updates.
- **Compact** — Cleans and optimizes family documents (.rfa). Removes unused materials and compact-saves. Validates `Document.IsFamilyDocument` before running. Uses Revit's built-in `TaskDialog` for confirmation (no WPF UI).

### Shared Layer

- `GeometryHelper` — geometry utilities: `IsOnVerticalFace`, `IsLineBasedFixture`, `GetHostFaceNormal`, `GetWallFaceNormal`, `GetElectricalConnector`, `IsWallSconce`, `IsReceptacle`.
- `ParameterHelper` — centralizes all Revit parameter reads (element and circuit parameters).
- `CalculationHelper` — aggregation utilities (e.g., `CalculateTotalLinearLength`).

### Known Namespace Collision

The `TurboSuite.Wire` namespace conflicts with `Autodesk.Revit.DB.Electrical.Wire`. Files that reference the Revit `Wire` type must use the alias `using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;`.

## Key Revit API Patterns

- All model modifications must occur inside a `Transaction`.
- Element queries use `FilteredElementCollector` with category filters (e.g., `OST_LightingDevices`, `OST_LightingFixtures`, `OST_ElectricalFixtures`, `OST_ElectricalCircuit`).
- Key built-in parameters: `RBS_ELEC_CIRCUIT_NUMBER`, `RBS_ELEC_CIRCUIT_NAME`, `RBS_ELEC_APPARENT_LOAD`, `RBS_ELEC_CIRCUIT_PANEL_PARAM`, `ALL_MODEL_TYPE_MARK`, `ALL_MODEL_MANUFACTURER`, `ALL_MODEL_INSTANCE_COMMENTS`.
- Custom parameters accessed by name: "Symbol Length", "Symbol Width", "Scale Factor", "Switch ID", "Linear Length", "Linear Power", "Power", "Sub-Driver Power", "Dimming Protocol", "Voltage", "Maximum Fixtures", "Remote Power Supply", "Load Classification Abbreviation", "Catalog Number1".
- Selection filtering uses `ISelectionFilter` implementations.
- Circuit filtering for Driver uses "Remote Power Supply" (Yes/No type parameter on Lighting Fixture types).
- **IMPORTANT**: Room name must be read via `room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()` — `room.Name` returns "Number Name" format and must not be used.

### PanelScheduleView API

Key methods:
- **Read**: `GetSlotNumberByCell`, `GetCellsBySlotNumber`, `GetCircuitIdByCell`, `IsSpare`, `IsSpace`
- **Write**: `MoveSlotTo` (check `CanMoveSlotTo` first), `AddSpare`, `AddSpace`, `RemoveSpare`, `RemoveSpace`
- **Create**: `PanelScheduleView.CreateInstanceView(doc, panelId)` — find existing first via `FilteredElementCollector.OfClass(typeof(PanelScheduleView))`
- `IsSlotGrouped` is **read-only** — there is no `GroupCircuits`/`UngroupCircuits` method in any Revit API version.
- `GetCellsBySlotNumber` returns multiple rows/cols for multi-pole breakers; use `slotRows.Last()` / `slotCols.First()` as the canonical anchor cell.

### Modal Dialogs and View Navigation

WPF windows shown via `ShowDialog()` block the Revit UI. Pattern: store the target view on the ViewModel, close the dialog, then call `uidoc.RequestViewChange(view)` from the `IExternalCommand` after `ShowDialog()` returns.

### WPF DataGrid Multi-Select in MVVM

`DataGrid.SelectedItems` is not a `DependencyProperty` and cannot be bound in XAML. Use a code-behind `SelectionChanged` event handler to push selections into a ViewModel list. **Pitfall**: do not set `SelectedRow` or fire `OnPropertyChanged(nameof(SelectedRow))` from within `SetSelectedRows` — the two-way `SelectedItem` binding will push `null` back, clearing multi-selection in a feedback loop.

## Dependencies

No external NuGet packages. References only:
- `RevitAPI.dll` and `RevitAPIUI.dll` (from `C:\Program Files\Autodesk\Revit 2025\`)
- .NET 8.0-windows / WPF assemblies

## Compact Instructions

When compacting context, always preserve:
- The full list of modified files in the current session
- Any build error output
- The current transaction/operation being implemented
- Active workflow rules (Explain Before Acting, Parameter Safety)
