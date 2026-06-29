# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Release Status

In production to ~5 users at CDLTG (current version in git tags). Future work ships via the network-share auto-update channel — assume every change reaches production users on their next Revit launch. Breaking changes to ExtensibleStorage schemas, parameter names, or settings shapes require a coordinated rollout (see "ExtensibleStorage Schema Changes" below).

## Project Overview

TurboSuite is a unified Autodesk Revit add-in for electrical/lighting automation, written in C#, supporting **Revit 2024 and 2025**. A per-version `TurboSuite.dll` (.NET 8.0-windows for 2025, .NET Framework 4.8 for 2024) implements `IExternalApplication` and registers fourteen shipped commands (TurboDriver, TurboRPS, TurboName, TurboBubble, TurboTag, TurboWire, TurboZones, TurboNumber, TurboCompact, TurboTab, TurboDocs, TurboMask, TurboSnoop, TurboSchedule) plus a Settings dialog across three ribbon panels (Settings, Commands, Utilities). Two more commands, TurboSetup and TurboDMX, are compiled in but gated behind `ExperimentalCommandsEnabled` in `App/TurboSuiteApplication.cs` (TurboDMX is mid-build — solve/placement/numbering-lock and the per-loop one-line generator all work; what's owed is in-Revit live testing of the one-line plus the author-once wire-legend artifact).

## Build Commands

```bash
dotnet build TurboSuite.sln
```

Platform target is **x64**. All Revit-coupled add-in source lives **once** in `Shim/` (a Visual Studio Shared Project — `Shim/Shim.projitems` imported by both csprojs; `Shim/Shim.shproj` is the VS node, never built by the CLI). It compiles into **two thin per-version shims** — `Revit2025/TurboSuite.Revit2025.csproj` (net8.0-windows, Revit 2025 API) and `Revit2024/TurboSuite.Revit2024.csproj` (net48, Revit 2024 API) — each emitting `TurboSuite.dll` via `AssemblyName` into its own `Addins\{year}\` folder. **The shared source carries no version constant: the Revit year comes from the running Revit at runtime** (`UIControlledApplication.ControlledApplication.VersionNumber`, captured in `OnStartup`), so compile-time divergence is confined to the csproj TFM/API refs plus two seam patterns:

- **Single shared file** (`Shim/.../ElementRefConversions`, the `.Value`↔`.IntegerValue` boundary): compiles for both because the member exists in each API, just differently typed.
- **Per-shim split file** (`Revit{year}/Setup/LinkGraphicsSeam.cs`): same namespace + class declared once under each `Revit{year}/`, each picked up only by its own shim's default globbing. Use this when an API member exists in *only one* version — e.g. the 2025-only RVT link *Custom* display settings, which the 2024 file stubs out.

Supporting these: version-agnostic, multi-targeted (`net48;net8.0-windows`) `Core/` and `Abstractions/` (no Revit refs), plus `Updater/` and `Installer/`. Tests live in `Tests/TurboSuite.Core.Tests.csproj` (xUnit, net8.0-windows; run `dotnet test`) — currently the pure TurboDMX `Core/Dmx/` oracle suite (the shims are validated by manual in-Revit testing). No linting configs.

To build just one channel (e.g. in CI or a quick check): `dotnet build Revit2025/TurboSuite.Revit2025.csproj` (or `Revit2024/...`). In Visual Studio, set the desired shim as startup project and F5 to launch that Revit.

To publish a release to the server share (run from non-admin PowerShell), **once per Revit version** into that version's share subfolder:
```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVER\ShareName\path\to\TurboSuite" -RevitVersion "2025" -Version "1.2.0"
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVER\ShareName\path\to\TurboSuite" -RevitVersion "2024" -Version "1.2.0"
```

**IMPORTANT**: Always use `dotnet.exe` (not `dotnet`) when running from WSL — the `.exe` suffix is required to invoke Windows executables. Always use Windows-style paths for `dotnet.exe`/MSBuild commands (e.g., `'C:\Users\jacobq\...\TurboSuite.Revit2025.csproj'`). Never use WSL-style `/mnt/c/...` paths — they cause `MSB1001` errors.

## Git Repository

- **Remote:** GitHub (CDLTG/TurboSuite3D), default branch `main`, **public repo** (GPL v3)
- **Ignored:** `Specs/` (local-only), `Installer/publish/` (build output), `bin/`, `obj/`, `.vs/`, `.idea/`
- Do NOT commit files from `Specs/` — they are historical reference documents kept locally only.
- Always commit and push `.gitignore` changes so they take effect on GitHub.

### Public Repository Security

**This repository is public (GPL v3).** Never hardcode secrets, credentials, real server paths/UNC paths, internal infrastructure details, or sensitive data. Use placeholders or environment variables.

## Deployment

Each shim's post-build target copies `TurboSuite.addin` and `TurboSuite.dll`/`.pdb` to its own per-version folder (`{year}` = the shim's Revit version, 2024 or 2025):
```
%APPDATA%\Autodesk\Revit\Addins\{year}\
%APPDATA%\Autodesk\Revit\Addins\{year}\TurboSuite\
```
It also copies the version-matched `TurboSuiteUpdater.exe` (net48 for 2024 — exe only; net8 for 2025 — exe + dll + runtimeconfig) to `%LOCALAPPDATA%\TurboSuite\{year}\`.

Revit auto-discovers `.addin` files from that directory on startup. The two channels are fully isolated — same DLL name, different per-version folders.

### Installation and Auto-Update

**First-time install:** Users run the combined `TurboSuiteInstaller.exe` from the network share root. It auto-discovers the per-version channel subfolders next to it (`\2024\`, `\2025\` — matched by a year-shape regex, so a future `\2027\` needs no installer change), and for each Revit version present it copies that channel's files to `Addins\{year}\`, writes a `config.json` to `%LOCALAPPDATA%\TurboSuite\{year}\` (ServerPath = that channel's share subfolder), and writes the initial `version.txt`. If no matching Revit is found it offers to install all available channels ahead of Revit.

**Auto-update:** On Revit launch, the year is captured in `OnStartup` and `UpdateService` reads the server path from `%LOCALAPPDATA%\TurboSuite\{year}\config.json`, checking that channel's subfolder for a newer `version.txt`. If found, files are staged to `%LOCALAPPDATA%\TurboSuite\{year}\Staging\`. The user is prompted to accept or skip. If accepted, the per-version `TurboSuiteUpdater.exe` applies the update after Revit closes. Skipped updates remain staged and prompt again on next launch.

**Publishing updates:** Run `publish.ps1` once per `-RevitVersion` from a non-admin PowerShell (admin sessions cannot see mapped network drives). Each run publishes one channel into `<ServerPath>\<year>\` (own `version.txt`, DLL set, version-matched updater, and `Archive\`); the combined installer is published once to the share root. Rollback is per-channel (`-Rollback <ver>`). See `PUBLISHING.md` for full syntax. Bump the version each release.

**Uninstall:** Users can run `TurboSuiteInstaller.exe` again and click "Uninstall" to remove all TurboSuite files for every installed Revit version.

Each shim's add-in source comes from the `Shim/Shim.projitems` import (a Shared Project), not a recursive glob — and the shims live in their own `Revit{year}/` subdirectories, so `Updater/`, `Installer/`, and the other channel sit outside the project's compile cone with no `DefaultItemExcludes` needed.

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

### ExtensibleStorage Schema Changes
When adding or removing fields in any storage service (`FamilyNameSettingsStorageService`, `CadRoomSourceStorageService`, etc.), create a **new schema GUID**. Old schemas are cached in Revit's memory and cannot be updated at runtime. After changing a GUID, the user must:
1. Close Revit
2. Build with the new code
3. Open Revit
4. Delete the stale `DataStorage` elements — see recovery procedure below
5. Open Settings, re-enter values, and save

**Recovery procedure:** To clear stale DataStorage during dev, use a one-shot RevitPythonShell / pyRevit script against the open document to run `FilteredElementCollector(doc).OfClass(typeof(DataStorage))` and delete the results inside a transaction. After deletion, restart Revit so cached `Schema.Lookup` results clear.

### Specification Documents
Versioned spec `.txt` files are in `Specs/`. Historical reference only — do NOT use them unless the user explicitly asks.

## Architecture

### Entry Point

`TurboSuite.App.TurboSuiteApplication` (IExternalApplication) registers ribbon panels under a "TurboSuite" tab. `SettingsCommand` opens a WPF dialog for family name settings stored in ExtensibleStorage.

### Namespace / Folder Structure

| Namespace | Purpose |
|-----------|---------|
| `TurboSuite.App` | Entry point, `SettingsCommand`, ViewModels, Views |
| `TurboSuite.Shared.Constants` | `ParameterNames` — centralized custom Revit parameter name strings |
| `TurboSuite.Shared.Converters` | WPF value converters shared across windowed commands |
| `TurboSuite.Shared.Filters` | `FixtureSelectionFilter`, `LightingFixtureTagFilter` |
| `TurboSuite.Shared.Helpers` | `GeometryHelper`, `ParameterHelper`, `NaturalStringComparer`, `FileLockHelper` |
| `TurboSuite.Shared.Models` | `WallLocalCoordinateSystem`, `FamilyNameSettings`, `CadRoomSourceSettings`, `GeneralSettings` |
| `TurboSuite.Shared.Services` | `DataStorageHelper`, `LinkedRoomFinderService`, `UpdateService`, settings storage/cache services |
| `TurboSuite.Shared.Styles` | Shared WPF ResourceDictionary styles |
| `TurboSuite.Shared.ViewModels` | `ViewModelBase`, `RelayCommand` |
| `TurboSuite.Name` | TurboName — room name assignment from linked DWG files (MVVM) |
| `TurboSuite.Driver` | TurboDriver (deploys power supplies, modal) + TurboRPS (staleness dashboard + batch in-place driver-type corrector, MVVM modeless). Right-click **Defer circuit** flags an intentionally-"wrong" config — stored in ExtensibleStorage **on the circuit element** (per-element entity, not the doc-singleton pattern), masking its verdict to DEFERRED and resurfacing as REVIEW if the snapshotted config later drifts |
| `TurboSuite.Bubble` | TurboBubble — switchleg tags and wires |
| `TurboSuite.Tag` | TurboTag — auto-places lighting fixture type tags |
| `TurboSuite.Wire` | TurboWire — circuit creation and wire routing |
| `TurboSuite.Zones` | TurboZones — load names and panel breakdown (MVVM, modeless) |
| `TurboSuite.Number` | TurboNumber — circuit numbers, keypads, power supply Switch IDs (MVVM, modeless) |
| `TurboSuite.Compact` | TurboCompact — family document cleanup |
| `TurboSuite.Docs` | TurboDocs — tabbed document generation: fixture schedule PDF, cut sheet PDF merging, control BOM PDF, load schedule PDF, panel schedule PDF, and cover/notes PDF (MVVM) |
| `TurboSuite.Tab` | TurboTab — document tab coloring (AvalonDock visual tree manipulation) |
| `TurboSuite.Mask` | TurboMask — masking region + per-fixture annotation stamps (from each family's nested Generic Annotation, loaded as `Stamp_*`) + detail-line overlays of wires connected to the masked devices. Real wires are left connected/hidden under the mask — overlays are view-only stand-ins, never a delete-recreate, so connectivity is untouched. Shipped (placeholder ribbon icon) |
| `TurboSuite.Setup` | TurboSetup — new-project setup: copy levels from the linked arch model, create Floor/RCP views with firm templates, configure RVT link display (gated behind `ExperimentalCommandsEnabled`; **3D RVT-linked only**; link-graphics path is Revit 2025-only, 2024 sets up levels/views/templates and leaves links manual) |
| `TurboSuite.Dmx` | TurboDMX — DMX-controlled RGBW LED tape/fixture automation (decoder/driver packing, addressing, one-line). **Gated behind `ExperimentalCommandsEnabled`; mid-build.** Pure engine + VMs in `Core/Dmx/` (unit-tested in `Tests/`); Revit-coupled half in `Shim/Dmx/`. Loop-centric modeless window (`DmxMainViewModel`): a zone pool of unassigned `Control Zone`s feeds a loops tree, with per-loop click-to-place (decoder+driver families), a Control-Zone-anchored numbering lock (`DmxLockReconciler`), per-zone cluster sub-builder (`DmxZoneBuilder`), and a per-loop view-owned one-line diagram (`Core/Dmx/OneLine/`, wipe-and-redraw). State persists in one JSON-backed ExtensibleStorage schema (`DmxStorageService`). Canonical design docs are local-only under `Specs/_DMX/` (`TurboDMX-BuildPlan.md` is the entry point) — consult them before working on DMX. |
| `TurboSuite.Snoop` | TurboSnoop — read-only "which VG checkbox do I uncheck?" reporter: pick a linked arch family, list the Visibility/Graphics **Category → Subcategory** checkboxes its geometry draws under (Model via one viewless `get_Geometry`; annotation via per-`ViewPlan` sweep + union). Deliberately **read-only, no Apply** — no API can flip the per-link VG checkbox, so it names the box and the user unchecks it (modeless, `ShowActivated=false`, keeps Revit's VG keybind live). Shipped (placeholder ribbon icon). Design rationale in `Shim/Snoop/` headers |
| `TurboSuite.Schedule` | TurboSchedule — page-per-Type-Mark form-view spec editor for lighting fixtures (`OST_LightingFixtures`) and drivers (`OST_LightingDevices`), unifying the two native spec schedules. Modeless WPF; data-driven `FieldDef` roster filtered by `PageKind`. `ScheduleTypeCollector` reconciles each field across all symbols sharing a Type Mark into a `SpecField` (states: n/a / read-only / ⟨varies⟩ / normal). Core can't reference Revit `StorageType`, so it maps to a Core `SpecValueKind` for display while the writer re-reads the live type at save (Integer params set via `Parameter.Set(int)`, not `SetValueString`; unit-bearing Doubles via `AsValueString`). Explicit Save flushes dirty pages in one transaction. Shipped (placeholder ribbon icon) |
| `Guide/` | `Guide.md` — user-facing documentation |
| `Updater/` | TurboSuiteUpdater — separate console app for applying auto-updates after Revit exits |
| `Installer/` | TurboSuiteInstaller — standalone WPF installer for network share deployment |

### Known Namespace Collision

`TurboSuite.Wire` conflicts with `Autodesk.Revit.DB.Electrical.Wire`. Use alias: `using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;`.

In `TurboSuite.Tab`, `Autodesk.Revit.DB.Color` conflicts with `System.Windows.Media.Color`. Use alias: `using Document = Autodesk.Revit.DB.Document;` (import only what's needed from Revit DB).

## Key Revit API Patterns

- All model modifications must occur inside a `Transaction`.
- Element queries use `FilteredElementCollector` with category filters (e.g., `OST_LightingDevices`, `OST_LightingFixtures`, `OST_ElectricalFixtures`).
- Key built-in parameters: `RBS_ELEC_CIRCUIT_NUMBER`, `RBS_ELEC_CIRCUIT_NAME`, `RBS_ELEC_APPARENT_LOAD`, `RBS_ELEC_CIRCUIT_PANEL_PARAM`, `ALL_MODEL_TYPE_MARK`, `ALL_MODEL_MANUFACTURER`, `ALL_MODEL_INSTANCE_COMMENTS`, `ALL_MODEL_MODEL`, `ALL_MODEL_MARK`, `ROOM_NAME`, `ROOM_NUMBER`.
- Custom parameters by name: "Switch ID", "Scale Factor", "Linear Length", "Linear Power", "Power", "Sub-Driver Power", "Derating Factor" (TurboDriver: Percentage type, the max fraction of rated sub-driver capacity to load to — e.g. `80%` reads as `0.8`; missing/0/out-of-range ⇒ no derate; applied only to the packing ceiling, never the sub-driver-count validity math), "Dimming Protocol", "Voltage", "Maximum Fixtures" (TurboDriver: `0` = no fixture-count limit), "Remote Power Supply", "Load Classification Abbreviation", "Load Classification", "Circuit Naming", "Circuit Prefix", "Circuit Prefix Separator", "Orientation", "Angle", "Two Gang", "Catalog Number1"–"Catalog Number6" (Counts resolves length tokens — `{xx}`, `{xx"}`, `{xxIN}`, `{ft}`, `{xx'}`, `{xxFT}`, `{xx'-xx"}`, `{xxFT-xxIN}`; feet formats truncate via integer divide by 12 — into catalog strings, with mutually exclusive cut-list modifiers: `max=N` (made-to-length cuts), `sizes=N1|N2|...` (per-instance discrete stock; use when each instance needs dedicated stock), and `pool=N1|N2|...` (sizes= but reuses offcuts across instances, 18" min reusable offcut; use when offcuts are fungible). Explodes one Worksheet row per unique resolved length. See the Counts cut-list code for exact semantics.), "Catalog Qty1"–"Catalog Qty6" (Counts per-slot quantity override — blank ⇒ Count, `N` ⇒ Count×N, `1/N` ⇒ ceil(Count/N), `N @type` ⇒ fixed N per type, `N @ft`/`N @in` ⇒ stock-cut Length mode `ceil(ceil(LinearLength×1.05)/stock)` where stock is the typed length in feet/inches — `@in` is normalized to feet at parse, so `16.40 @ft` and `196.80 @in` are identical; cannot coexist with a Catalog Number length token on the same slot), "Data Sheet URL", "Manufacturer". **Access via `TurboSuite.Shared.Constants.ParameterNames` — do NOT pass string literals to `LookupParameter`.**
- **IMPORTANT**: Room name must be read via `room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()` — `room.Name` returns "Number Name" format.

### API Limitations

- **Wire end display**: An `ElectricalWire` end vertex is pinned to the connector center (Revit re-inserts it there if moved); whether Revit visually clips the wire at the family boundary vs. draws it to center (a "tail") is a display-only decision made during **post-commit regeneration**, not controllable via the points passed to `ElectricalWire.Create`. Wire each connection in its **own committed transaction** (like the manual workflow) — batching wires in one shared transaction leaves the *last* wire's terminal end unclipped, drawn into the final element. See `DeploymentExecutor` wiring loop and the `WireCreationService` switch/sconce nudge.
- **Switch Systems** (`OST_SwitchSystem`) cannot be created or modified via the public API. Workaround: TurboDriver sets "Switch ID" parameter; users create switch systems manually. A device on a switch system reports an extra logical (`DomainUndefined`, no-origin) connector — so `ConnectorManager` count > the family's defined connectors; copies don't carry switch-system membership.
- **`PanelScheduleView.IsSlotGrouped`** is read-only — no `GroupCircuits`/`UngroupCircuits` API exists.
- **Light Group** writes require a `Transaction` — calling outside one crashes Revit (hard crash, not exception). Groups are not elements and cannot be found via `FilteredElementCollector`.
- **TextNote rotation**: `TextNote.Create` auto-orients text to be readable in the active view at orthogonal Project North angles (0°, ±90°, 180°). Manually rotating by `-ProjectPosition.Angle` at these angles doubles the rotation. Only apply rotation correction for non-orthogonal angles.

### WPF Patterns

- Modal `ShowDialog()` blocks Revit UI. Pattern: store target view on ViewModel, close dialog, call `uidoc.RequestViewChange(view)` after return.
- **Modeless pattern** (TurboNumber, TurboZones): `window.Show()` with `IExternalEventHandler` for all Revit API calls. ViewModels queue typed `RevitApiRequest` objects, call `ExternalEvent.Raise()`, and receive results via completion callbacks dispatched to the WPF thread. Chain sequential requests in callbacks — never raise two events simultaneously (second is silently dropped). For auto-save sites that fire on many UI events (e.g. TurboZones panel breakdown), coalesce raises via a `_savePending`/`_saveDirty` pair and re-raise from the completion callback when dirty, so the latest snapshot always lands.
- `DataGrid.SelectedItems` cannot be bound in XAML. Use code-behind `SelectionChanged` handler. Do not set `SelectedRow` from within `SetSelectedRows` — causes feedback loop clearing multi-selection.
- **TurboTab**: Walks Revit's AvalonDock visual tree via `MainWindowHandle` to color document tabs. Caches original `TabItem.Style` before modification and restores on toggle-off — never use `ClearValue(StyleProperty)`.

## Dependencies

- `RevitAPI.dll`, `RevitAPIUI.dll`, `Xceed.Wpf.AvalonDock.dll` (from the matching Revit 2024/2025 install, per shim)
- `ACadSharp` (NuGet) — DWG/DXF reading for TurboName
- `PdfSharpCore` (NuGet) — PDF operations for TurboDocs
- .NET 8.0-windows (Revit 2025 shim) / .NET Framework 4.8 (Revit 2024 shim) / Core multi-targets both / WPF
