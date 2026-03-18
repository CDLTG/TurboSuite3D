# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TurboSuite is a unified Autodesk Revit 2025 add-in for electrical/lighting automation, written in C#. It consolidates nine commands (TurboDriver, TurboRPS, TurboName, TurboBubble, TurboTag, TurboWire, TurboZones, TurboNumber, TurboCompact) plus a Settings dialog into a single `TurboSuite.dll` targeting .NET 8.0-windows. The add-in implements `IExternalApplication` to register three ribbon panels (Settings, Commands, Utilities) with ten `IExternalCommand` buttons.

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

`TurboSuite.App.TurboSuiteApplication` (IExternalApplication) registers ribbon panels under a "TurboSuite" tab: Settings (far left), Commands, Utilities, and Debug. `TurboSuite.App.SettingsCommand` opens a WPF dialog for configuring family name settings stored in ExtensibleStorage.

### Namespace / Folder Structure

| Namespace | Purpose |
|-----------|---------|
| `TurboSuite.App` | Entry point (`TurboSuiteApplication`), `SettingsCommand`, ViewModels, Views |
| `TurboSuite.Shared.Helpers` | `GeometryHelper`, `ParameterHelper` |
| `TurboSuite.Shared.Filters` | `FixtureSelectionFilter` (accepts both Lighting + Electrical Fixtures), `LightingFixtureTagFilter` |
| `TurboSuite.Shared.Models` | `WallLocalCoordinateSystem`, `FamilyNameSettings`, `CadRoomSourceSettings` |
| `TurboSuite.Shared.Services` | `DataStorageHelper`, `LinkedRoomFinderService`, `FamilyNameSettingsStorageService`, `FamilyNameSettingsCache`, `CadRoomSourceSettingsCache`, `CadRoomSourceStorageService` |
| `TurboSuite.Shared.ViewModels` | `ViewModelBase`, `RelayCommand` (shared MVVM base classes) |
| `TurboSuite.Name` | `NameCommand` (TurboName) + Services, Models, ViewModels, Views (MVVM) |
| `TurboSuite.Driver` | `DriverCommand` (TurboDriver), `RPSCommand` (TurboRPS) + Services, Models, ViewModels, Views |
| `TurboSuite.Bubble` | `BubbleCommand` + Placement calculators, Services, Constants, Filters |
| `TurboSuite.Tag` | `TagCommand` + Services, Helpers, Constants |
| `TurboSuite.Wire` | `WireCommand` + Services, Helpers, Constants, Views |
| `TurboSuite.Zones` | `ZonesCommand` + Services, Models, ViewModels, Views (MVVM) |
| `TurboSuite.Number` | `NumberCommand` + Services, Models, ViewModels, Views (MVVM) |
| `TurboSuite.Compact` | `CompactCommand` — family document cleanup (no UI beyond TaskDialog) |

### Command Modules

- **Name** (MVVM) — `TurboNameWindow` with two sections: "Assign Room Names" (active) and "Generate Regions" (under construction). Reads linked DWG files via ACadSharp to extract room names and ceiling heights from block attributes (Block mode) or layer-based text (Text mode). Assigns room names to "Room Region" type `FilledRegion` Comments and places `TextNote` elements at CAD source locations. Supports re-run safety (skips regions with existing TextNotes), ambiguity detection (multiple distinct room names in one region), ceiling description preservation (Vault, Slope, Barrel, etc.). Ceiling descriptions are placed as separate smaller TextNotes (`AL_Annotation_3"`). Uses `CadRoomSourceSettings` from shared ExtensibleStorage for per-document configuration.
- **Driver** — Contains two commands sharing the same services and models:
  - **TurboDriver** (`DriverCommand`) — Headless command: pre-select lighting fixtures with RPS, deploys recommended power supplies (place, circuit-connect, set Switch ID, tag, wire between multi-driver chains). Creates circuit if needed. Deletes and replaces existing power supplies (and their wires) on re-run. Applies per-view color overrides to fixtures and power supplies to visualize driver assignments (auto-cleared on next run). When `GeneralSettings.AutoSplitFixtures` is enabled, `FixtureSplitService` physically splits work-plane-hosted line-based fixtures to match the bin-packing algorithm's segment splits. Face-hosted (3D) fixtures are skipped — see `Specs/AutoSplitFixtures_3D_Research.txt` for API limitations. After deployment, split fixtures are re-tagged with the original `AL_Tag_Lighting Fixture (Linear Length)` tag type (Tag_Top/Tag_Bottom preserved) using TurboTag's 5" perpendicular offset logic.
  - **TurboRPS** (`RPSCommand`, MVVM) — Review window for inspecting power supply assignments across all RPS circuits. `DriverSelectionService` recommends driver types by matching fixture wattage, manufacturer, dimming protocol, and voltage. Uses First-Fit Decreasing bin-packing with recursive fixture splitting. Opens `TurboRPSWindow`.
- **Bubble** — Creates switchleg tags and wires for lighting fixtures and electrical fixtures. Lighting Fixtures are selected via their tags and use strategy pattern with `IPlacementCalculator` (Horizontal, LineBased, VerticalFace). Electrical Fixtures are selected directly; default path places tag left/right along localX, while vertical families (Exhaust, Fireplace Igniter) place tag up/down along localY with arc-approximated wire vertices. Uses `BubbleSelectionFilter` to accept both element types.
- **Tag** — Auto-places lighting fixture type tags. Handles point-based, line-based, and face-based fixtures.
- **Wire** — Creates electrical circuits and wire connections between lighting fixtures and electrical fixtures. Pre-selected fixtures: analyzes circuit state, creates/joins circuits as needed (assigns to last-used panel via highest `ElementId` heuristic), creates wires with arc routing, then shows a `CommentsDialog` for circuit comments (with autofill from existing comments in the document). Rejects selections spanning multiple circuits. Single fixture: creates circuit and prompts for comment (skips silently if comment already exists). Manual two-pick selection: wires only, no circuit creation. Multi-fixture ordering uses nearest-neighbor from both endpoints (double-farthest-point method), picking the shorter total path. Arc direction priority: (1) tag perpendicular component if `|dot| >= 0.3` and tags agree, (2) bulge away from group centroid, (3) default. Wall sconces and receptacles use spline routing with wall-normal offsets (sconces 2.5", receptacles 3"). Fixtures are grouped by category — no mixing of Lighting Fixtures and Electrical Fixtures in a single circuit or wire chain. Comments dialog can be disabled via `GeneralSettings.ShowCircuitCommentsDialog` toggle in TurboSuite Settings.
- **Zones** (MVVM) — Two-tab `TurboZonesWindow`: Tab 1 (Load Names) manages circuit load names with room/label resolution; Tab 2 (Panel Breakdown) visualizes dimmer module/panel allocation with brand configs (Lutron, Crestron).
- **Number** (MVVM) — Three-tab `TurboNumberWindow`: Tab 1 (Circuit Numbers) manages panel schedule slot layouts via `PanelScheduleView`; Tab 2 (Keypads) edits Switch ID on keypad devices with drag-drop room ordering; Tab 3 (Power Supplies) edits Switch ID on devices with "Sub-Driver Power" parameter. All tabs support auto-numbering with cascading sequential updates.
- **Compact** — Cleans and optimizes family documents (.rfa). Removes unused materials and compact-saves. Validates `Document.IsFamilyDocument` before running. Uses Revit's built-in `TaskDialog` for confirmation (no WPF UI).

### Shared Layer

- `GeometryHelper` — geometry utilities: `IsOnVerticalFace`, `IsLineBasedFixture`, `GetHostFaceNormal`, `GetWallFaceNormal`, `GetElectricalConnector`, `IsWallSconce`, `IsVerticalFamily`, `IsReceptacle`, `GetFixtureLocation`, `GetFixtureLocationRotation`, `GetSymbolExtents`, `GetSymbolExtentInDirection`.
- `ParameterHelper` — centralizes all Revit parameter reads (element and circuit parameters).
- `FamilyNameSettingsCache` / `FamilyNameSettingsStorageService` — per-document configurable family name lists (wall sconces, receptacles, vertical electrical families, vertical lighting families) stored in ExtensibleStorage. `IsWallSconce`, `IsVerticalFamily`, and `IsReceptacle` read from this cache rather than hardcoded strings.
- `GeneralSettingsCache` / `GeneralSettingsStorageService` — per-document boolean toggles (e.g., `ShowCircuitCommentsDialog`) stored in ExtensibleStorage with a separate schema from family name settings.
- `LinkedRoomFinderService` — finds the Room containing a fixture, checking host doc then linked docs. Includes `RoomLookupCache` inner class for batch lookups.
- `DataStorageHelper` — shared `FindDataStorage` utility for ExtensibleStorage queries.

### Known Namespace Collision

The `TurboSuite.Wire` namespace conflicts with `Autodesk.Revit.DB.Electrical.Wire`. Files that reference the Revit `Wire` type must use the alias `using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;`.

## Key Revit API Patterns

- All model modifications must occur inside a `Transaction`.
- Element queries use `FilteredElementCollector` with category filters (e.g., `OST_LightingDevices`, `OST_LightingFixtures`, `OST_ElectricalFixtures`, `OST_ElectricalCircuit`).
- Key built-in parameters: `RBS_ELEC_CIRCUIT_NUMBER`, `RBS_ELEC_CIRCUIT_NAME`, `RBS_ELEC_APPARENT_LOAD`, `RBS_ELEC_CIRCUIT_PANEL_PARAM`, `ALL_MODEL_TYPE_MARK`, `ALL_MODEL_MANUFACTURER`, `ALL_MODEL_INSTANCE_COMMENTS`.
- Custom parameters accessed by name: "Scale Factor", "Switch ID", "Linear Length", "Linear Power", "Power", "Sub-Driver Power", "Dimming Protocol", "Voltage", "Maximum Fixtures", "Remote Power Supply", "Load Classification Abbreviation", "Catalog Number1".
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

### Switch System API Limitations

Revit Switch Systems (`MEPSystem` with category `OST_SwitchSystem`) **cannot be created or modified via the public API**. Confirmed infeasible:
- **No creation API**: No `MEPSystem.Create` static method, no `ElectricalSystem.Create` for switch type, no `PostableCommand` for SwitchSystem.
- **Cannot add fixtures**: `MEPSystem.Add(ConnectorSet)` rejects connectors already consumed by an electrical circuit (`ArgumentException: Some connectors to be added into the system have been used`).
- **Cannot assign base equipment**: `MEPSystem.BaseEquipment` is read-only — no setter to programmatically assign a power supply as the "switch" for a system.
- **Workaround**: TurboDriver sets the "Switch ID" parameter on placed power supplies and applies color overrides (`View.SetElementOverrides`) to visually group fixtures by driver assignment. Users must manually create/assign switch systems in the Revit UI afterward. Matching Switch ID values on the same circuit cause Revit to auto-associate devices when creating switch systems manually. Color overrides are auto-cleared on the next TurboDriver invocation via `VisualFeedbackService.ClearPreviousOverrides`.

### Light Group API

Light Groups (`Autodesk.Revit.DB.Lighting`) are document-level fixture collections for rendering/analysis. Access via `LightGroupManager.GetLightGroupManager(doc)`. Full CRUD supported: `CreateGroup`, `DeleteGroup`, `AddLight`, `RemoveLight`, rename via `Name` setter. All writes require a `Transaction` — calling outside a transaction crashes Revit (hard crash, not an exception). On/off and dimmer methods require a `View3D`; CRUD works from any view. Fixtures can belong to multiple groups. Groups are not Revit elements — they cannot be found via `FilteredElementCollector`. See `Specs/LightGroupAPI.txt` for full method signatures.

### Modal Dialogs and View Navigation

WPF windows shown via `ShowDialog()` block the Revit UI. Pattern: store the target view on the ViewModel, close the dialog, then call `uidoc.RequestViewChange(view)` from the `IExternalCommand` after `ShowDialog()` returns.

### WPF DataGrid Multi-Select in MVVM

`DataGrid.SelectedItems` is not a `DependencyProperty` and cannot be bound in XAML. Use a code-behind `SelectionChanged` event handler to push selections into a ViewModel list. **Pitfall**: do not set `SelectedRow` or fire `OnPropertyChanged(nameof(SelectedRow))` from within `SetSelectedRows` — the two-way `SelectedItem` binding will push `null` back, clearing multi-selection in a feedback loop.

## Dependencies

- `RevitAPI.dll` and `RevitAPIUI.dll` (from `C:\Program Files\Autodesk\Revit 2025\`)
- `ACadSharp` (NuGet) — .NET library for reading/writing AutoCAD DWG/DXF files without AutoCAD. Used by TurboName's `CadRoomExtractorService` to parse linked DWG files and extract block attributes, text entities, and coordinate data.
- .NET 8.0-windows / WPF assemblies

## Compact Instructions

When compacting context, always preserve:
- The full list of modified files in the current session
- Any build error output
- The current transaction/operation being implemented
- Active workflow rules (Explain Before Acting, Parameter Safety)
