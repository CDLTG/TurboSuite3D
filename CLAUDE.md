# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TurboSuite is a unified Autodesk Revit 2025 add-in for electrical/lighting automation, written in C#. It consolidates eleven commands (TurboDriver, TurboRPS, TurboName, TurboBubble, TurboTag, TurboWire, TurboZones, TurboNumber, TurboCompact, TurboTab, TurboCuts) plus a Settings dialog into a single `TurboSuite.dll` targeting .NET 8.0-windows. The add-in implements `IExternalApplication` to register four ribbon panels (Settings, Commands, Utilities, Debug) with twelve `IExternalCommand` buttons.

## Build Commands

```bash
dotnet build TurboSuite.sln
```

Platform target is **x64**. The solution contains two projects: `TurboSuite.csproj` (main add-in) and `Updater/TurboSuiteUpdater.csproj` (auto-update helper). There are no automated tests or linting configurations.

**IMPORTANT**: Always use Windows-style paths for `dotnet`/MSBuild commands (e.g., `'C:\Users\jacobq\...\TurboSuite.csproj'`). Never use WSL-style `/mnt/c/...` paths — they cause `MSB1001` errors.

## Git Repository

- **Remote:** GitHub (CDLTG/TurboSuite3D), default branch `main`, **public repo** (GPL v3)
- **Ignored:** `Specs/` (local-only reference docs), `bin/`, `obj/`, `.vs/`, `.idea/`
- Do NOT commit files from `Specs/` — they are historical reference documents kept locally only.
- Always commit and push `.gitignore` changes so they take effect on GitHub.

### Public Repository Security

**This repository is public.** Never introduce code that could become a security risk when the source is visible:
- **No secrets**: Never hardcode passwords, API keys, tokens, credentials, or real server paths/UNC paths. Use placeholders or environment variables.
- **No internal infrastructure details**: Do not commit real server names, IP addresses, network shares, or internal URLs.
- **No sensitive data**: Do not commit user data, company-specific configuration, or proprietary file paths.
- **Safe coding**: Sanitize any user input, avoid path traversal vulnerabilities, and follow secure coding practices for file I/O and network operations.

## Deployment

The post-build target copies `TurboSuite.addin` and `TurboSuite.dll`/`.pdb` to:
```
%APPDATA%\Autodesk\Revit\Addins\2025\
%APPDATA%\Autodesk\Revit\Addins\2025\TurboSuite\
```
It also copies `TurboSuiteUpdater.exe` to `%LOCALAPPDATA%\TurboSuite\`.

Revit auto-discovers `.addin` files from that directory on startup.

### Auto-Update (not yet finalized)

An auto-update system checks a shared server (`UpdateConstants.ServerPath`) on Revit launch. If a newer `version.txt` is found, files are staged to `%LOCALAPPDATA%\TurboSuite\Staging\`. The user is prompted to accept or skip. If accepted, `TurboSuiteUpdater.exe` applies the update after Revit closes. Skipped updates remain staged and prompt again on next launch. The server path in `UpdateConstants.cs` is a placeholder (`\\SERVER_NAME\TurboSuite`) — set it before deploying.

The `Updater/` subdirectory is excluded from the main project via `<DefaultItemExcludes>` in `TurboSuite.csproj` to prevent the WPF temp project from picking up Updater source files.

## Workflow Rules

### 3D and 2D Drafting Support
**IMPORTANT**: All TurboSuite commands MUST work in both workflows:
- **3D Model**: Hosted families (ceiling, wall, floor, line-based) with 3D geometry in plan/RCP views. Walls, ceilings, floors, and other host elements are **always in a linked model** (RevitLinkInstance) unless otherwise indicated. Do NOT check `Host is Wall` etc. — use `HostFace != null` to detect hosted families.
- **2D Drafting**: Unhosted families with no 3D geometry, placed in Floor Plan views over linked 2D CAD files. Same parameters/connectors, but no hosting and no Room elements.

When implementing new features or modifying existing commands:
1. Handle `fixture.Host == null` and `fixture.HostFace == null` (unhosted families).
2. Do NOT assume fixtures have a host, a host face normal, or a LocationCurve.
3. Room resolution returns null in 2D projects — callers already handle null gracefully.

### Fixture Transform and Direction Offsets
When converting fixture-local offsets to global coordinates:
- **Use `BasisX` rotation angle only** — do NOT use the full transform with `BasisX * localX + BasisY * localY + BasisZ * localZ`. BasisY/BasisZ are inverted for ceiling-hosted fixtures, causing direction flips.
- **Pattern**: `Math.Atan2(transform.BasisX.Y, transform.BasisX.X)` → 2D rotation. See `TagPlacementService.TransformToGlobal`.
- **RCP views do NOT reflect the X axis**. Do NOT negate X for RCP views.

### Explain Before Acting
When asked to explain something, provide the explanation only. Do not assume a code change, behavior modification, or memory update is wanted unless explicitly requested.

### Revit API Parameter Safety
**IMPORTANT**: Before implementing anything that reads or writes a Revit parameter:
1. Verify the parameter is writable via the Revit API (some are read-only or computed).
2. List any known restrictions, limitations, or alternative approaches.
3. For `ElementId` storage type parameters, probe valid `ElementId` values rather than assuming string or integer assignment will work.
4. Only proceed to implementation after confirming feasibility.

### ExtensibleStorage Schema Changes (Temporary — remove after TurboSuite deployment)
When adding or removing fields in `FamilyNameSettingsStorageService`, do NOT create a new schema GUID. Keep the existing GUID and remind the user to purge the existing schema from their Revit documents before testing.

### Specification Documents
Versioned spec `.txt` files are in `Specs/`. Historical reference only — do NOT use them unless the user explicitly asks.

## Architecture

### Entry Point

`TurboSuite.App.TurboSuiteApplication` (IExternalApplication) registers ribbon panels under a "TurboSuite" tab. `SettingsCommand` opens a WPF dialog for family name settings stored in ExtensibleStorage.

### Namespace / Folder Structure

| Namespace | Purpose |
|-----------|---------|
| `TurboSuite.App` | Entry point, `SettingsCommand`, ViewModels, Views |
| `TurboSuite.Shared.Helpers` | `GeometryHelper`, `ParameterHelper` |
| `TurboSuite.Shared.Filters` | `FixtureSelectionFilter`, `LightingFixtureTagFilter` |
| `TurboSuite.Shared.Models` | `WallLocalCoordinateSystem`, `FamilyNameSettings`, `CadRoomSourceSettings` |
| `TurboSuite.Shared.Services` | `DataStorageHelper`, `LinkedRoomFinderService`, `UpdateService`, settings storage/cache services |
| `TurboSuite.Shared.ViewModels` | `ViewModelBase`, `RelayCommand` |
| `TurboSuite.Name` | TurboName — room name assignment from linked DWG files (MVVM) |
| `TurboSuite.Driver` | TurboDriver + TurboRPS — power supply deployment and review (MVVM) |
| `TurboSuite.Bubble` | TurboBubble — switchleg tags and wires |
| `TurboSuite.Tag` | TurboTag — auto-places lighting fixture type tags |
| `TurboSuite.Wire` | TurboWire — circuit creation and wire routing |
| `TurboSuite.Zones` | TurboZones — load names and panel breakdown (MVVM) |
| `TurboSuite.Number` | TurboNumber — circuit numbers, keypads, power supply Switch IDs (MVVM, modeless) |
| `TurboSuite.Compact` | TurboCompact — family document cleanup |
| `TurboSuite.Cuts` | TurboCuts — spec sheet PDF download, stamping, and merging (MVVM) |
| `TurboSuite.Tab` | TurboTab — document tab coloring (AvalonDock visual tree manipulation) |
| `Updater/` | TurboSuiteUpdater — separate console app for applying auto-updates after Revit exits |

### Known Namespace Collision

`TurboSuite.Wire` conflicts with `Autodesk.Revit.DB.Electrical.Wire`. Use alias: `using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;`.

In `TurboSuite.Tab`, `Autodesk.Revit.DB.Color` conflicts with `System.Windows.Media.Color`. Use alias: `using Document = Autodesk.Revit.DB.Document;` (import only what's needed from Revit DB).

## Key Revit API Patterns

- All model modifications must occur inside a `Transaction`.
- Element queries use `FilteredElementCollector` with category filters (e.g., `OST_LightingDevices`, `OST_LightingFixtures`, `OST_ElectricalFixtures`).
- Key built-in parameters: `RBS_ELEC_CIRCUIT_NUMBER`, `RBS_ELEC_CIRCUIT_NAME`, `RBS_ELEC_APPARENT_LOAD`, `RBS_ELEC_CIRCUIT_PANEL_PARAM`, `ALL_MODEL_TYPE_MARK`, `ALL_MODEL_MANUFACTURER`, `ALL_MODEL_INSTANCE_COMMENTS`.
- Custom parameters by name: "Scale Factor", "Switch ID", "Linear Length", "Linear Power", "Power", "Sub-Driver Power", "Dimming Protocol", "Voltage", "Maximum Fixtures", "Remote Power Supply", "Load Classification Abbreviation", "Catalog Number1", "Data Sheet URL".
- **IMPORTANT**: Room name must be read via `room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()` — `room.Name` returns "Number Name" format.

### API Limitations

- **Switch Systems** (`OST_SwitchSystem`) cannot be created or modified via the public API. Workaround: TurboDriver sets "Switch ID" parameter; users create switch systems manually.
- **`PanelScheduleView.IsSlotGrouped`** is read-only — no `GroupCircuits`/`UngroupCircuits` API exists.
- **Light Group** writes require a `Transaction` — calling outside one crashes Revit (hard crash, not exception). Groups are not elements and cannot be found via `FilteredElementCollector`.

### WPF Patterns

- Modal `ShowDialog()` blocks Revit UI. Pattern: store target view on ViewModel, close dialog, call `uidoc.RequestViewChange(view)` after return.
- **Modeless pattern** (TurboNumber): `window.Show()` with `IExternalEventHandler` for all Revit API calls. ViewModels queue typed `RevitApiRequest` objects, call `ExternalEvent.Raise()`, and receive results via completion callbacks dispatched to the WPF thread. Chain sequential requests in callbacks — never raise two events simultaneously (second is silently dropped).
- `DataGrid.SelectedItems` cannot be bound in XAML. Use code-behind `SelectionChanged` handler. Do not set `SelectedRow` from within `SetSelectedRows` — causes feedback loop clearing multi-selection.
- **TurboTab pattern**: Uses `UIApplication.MainWindowHandle` + `HwndSource.FromHwnd()` to get the WPF root visual, walks the AvalonDock visual tree to find `DockingManager` → `LayoutDocumentPaneControl` → `TabItem`. Maps tabs to documents via reflection on private MFC pointers (`getMFCDoc` / `GetPointerValue`). Groups tabs by project (same color), distinguishes family documents via `Document.IsFamilyDocument`. Caches original `TabItem.Style` before modification and restores on toggle-off — never use `ClearValue(StyleProperty)`. Auto-starts via `Idling` event with retry (UI not ready during `OnStartup`). Persists enabled state to `%APPDATA%\TurboSuite\TurboTabSettings.json`.

## Dependencies

- `RevitAPI.dll` and `RevitAPIUI.dll` (from `C:\Program Files\Autodesk\Revit 2025\`)
- `Xceed.Wpf.AvalonDock.dll` (from `C:\Program Files\Autodesk\Revit 2025\`) — ships with Revit, used by TurboTab for document tab coloring
- `ACadSharp` (NuGet) — reads AutoCAD DWG/DXF files. Used by TurboName.
- `PdfSharpCore` (NuGet) — PDF reading, stamping, and merging. Used by TurboCuts.
- .NET 8.0-windows / WPF assemblies

## Compact Instructions

When compacting context, always preserve:
- The full list of modified files in the current session
- Any build error output
- The current transaction/operation being implemented
- Active workflow rules (Explain Before Acting, Parameter Safety)
