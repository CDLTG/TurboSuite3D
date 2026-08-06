# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Release Status

In production to ~5 users at CDLTG (current version in git tags). Future work ships via the network-share auto-update channel — assume every change reaches production users on their next Revit launch. Breaking changes to ExtensibleStorage schemas, parameter names, or settings shapes require a coordinated rollout (see "ExtensibleStorage Schema Changes" below).

## Project Overview

TurboSuite is a unified Autodesk Revit add-in for electrical/lighting automation, written in C#, supporting **Revit 2024 and 2025**. A per-version `TurboSuite.dll` (.NET 8.0-windows for 2025, .NET Framework 4.8 for 2024) implements `IExternalApplication` and registers fifteen shipped commands (TurboDriver, TurboRPS, TurboName, TurboBubble, TurboTag, TurboWire, TurboZones, TurboNumber, TurboCompact, TurboTab, TurboDocs, TurboMask, TurboSnoop, TurboSchedule, TurboSetup) plus a Settings dialog across three ribbon panels (Settings, Commands, Utilities). One more command, TurboDMX, is compiled in but gated behind `ExperimentalCommandsEnabled` in `App/TurboSuiteApplication.cs`.

## Build Commands

```bash
dotnet build TurboSuite.sln
```

Platform target is **x64**. All Revit-coupled add-in source lives **once** in `Shim/` (a Visual Studio Shared Project — `Shim/Shim.projitems` imported by both csprojs; `Shim/Shim.shproj` is the VS node, never built by the CLI). It compiles into **two thin per-version shims** — `Revit2025/TurboSuite.Revit2025.csproj` (net8.0-windows, Revit 2025 API) and `Revit2024/TurboSuite.Revit2024.csproj` (net48, Revit 2024 API) — each emitting `TurboSuite.dll` via `AssemblyName` into its own `Addins\{year}\` folder. **The shared source carries no version constant: the Revit year comes from the running Revit at runtime** (`UIControlledApplication.ControlledApplication.VersionNumber`, captured in `OnStartup`), so compile-time divergence is confined to the csproj TFM/API refs plus two seam patterns:

- **Single shared file** (`Shim/.../ElementRefConversions`, the `.Value`↔`.IntegerValue` boundary): compiles for both because the member exists in each API, just differently typed.
- **Per-shim split file** (`Revit{year}/Setup/LinkGraphicsSeam.cs`): same namespace + class declared once under each `Revit{year}/`, each picked up only by its own shim's default globbing. Use this when an API member exists in *only one* version — e.g. the 2025-only RVT link *Custom* display settings, which the 2024 file stubs out.

Supporting these: version-agnostic, multi-targeted (`net48;net8.0-windows`) `Core/` and `Abstractions/` (no Revit refs), plus `Updater/` and `Installer/`. Tests live in `Tests/TurboSuite.Core.Tests.csproj` (xUnit, net8.0-windows; run `dotnet test`) — oracle suites over the pure, Revit-free logic in `Core/`, expanding as more lands; the shims are validated by manual in-Revit testing. Core exposes internals to the test assembly via `<InternalsVisibleTo>` (compile-time only) so internal helpers can be pinned directly. No linting configs.

To build just one channel (e.g. in CI or a quick check): `dotnet build Revit2025/TurboSuite.Revit2025.csproj` (or `Revit2024/...`). In Visual Studio, set the desired shim as startup project and F5 to launch that Revit.

To publish a release to the server share (run from non-admin PowerShell), **once per Revit version** into that version's share subfolder:
```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVER\ShareName\path\to\TurboSuite" -RevitVersion "2025" -Version "1.2.0"
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -ServerPath "\\SERVER\ShareName\path\to\TurboSuite" -RevitVersion "2024" -Version "1.2.0"
```

**IMPORTANT**: Always use `dotnet.exe` (not `dotnet`) when running from WSL — the `.exe` suffix is required to invoke Windows executables. Pass Windows-style **relative** paths from the repo root (e.g., `'Revit2025\TurboSuite.Revit2025.csproj'`, backslash separators) — WSL interop resolves them against the current directory. Never use WSL-style `/mnt/c/...` absolute paths — they cause `MSB1001` errors.

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

Every install/update path is **per-channel**: the combined `TurboSuiteInstaller.exe` sits at the share root and auto-discovers year-shaped subfolders next to it (`\2024\`, `\2025\` — a future `\2027\` needs no installer change), installing each to `Addins\{year}\` with its own `config.json` + `version.txt` under `%LOCALAPPDATA%\TurboSuite\{year}\`. On Revit launch `UpdateService` reads that channel's `config.json`, stages any newer build to `Staging\`, and prompts; accepted updates are applied by the version-matched `TurboSuiteUpdater.exe` after Revit closes, skipped ones stay staged and re-prompt. The same exe uninstalls all versions.

`publish.ps1` must run once per `-RevitVersion` from a **non-admin** PowerShell (admin sessions cannot see mapped network drives); rollback is per-channel (`-Rollback <ver>`). Bump the version each release. See `PUBLISHING.md` for full syntax and `Installer/README.md` for installer internals.

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

### TurboSpike — Diagnostic Bench
`Shim/Spike/SpikeCommand.cs` is a throwaway diagnostic command, gated behind `ExperimentalCommandsEnabled` so it surfaces every dev session and ships disabled. Rules:
1. **Spike over guessing.** When the running model can answer a question you'd otherwise trial-and-error or assume (a parameter's `StorageType`/writability, whether an API member exists on this version, a family's connectors/geometry), write a probe into `SpikeCommand.Execute`, have the user build and run it, and read the dialog — before writing the targeted code.
2. **Overwrite-safe by design.** Everything in `SpikeCommand.Execute` is diagnostics-only scratch. Never store logic there that anything else depends on, and never treat its contents as worth preserving.
3. **Clobber freely.** When you need TurboSpike, overwrite whatever probe is already in the `Execute` body without asking — no prior spike is worth keeping.

### Specification Documents
Versioned spec `.txt` files are in `Specs/`. Historical reference only — do NOT use them unless the user explicitly asks.

## Architecture

### Entry Point

`TurboSuite.App.TurboSuiteApplication` (IExternalApplication) registers ribbon panels under a "TurboSuite" tab. `SettingsCommand` opens a WPF dialog for family name settings stored in ExtensibleStorage.

### Namespace / Folder Structure

Each shipped module keeps its own `Shim/<Module>/README.md` — workflow, design rationale, and gotchas live **there**, not in this table. Read the module README before working in a module. The table is a routing index only.

| Namespace | Purpose |
|-----------|---------|
| `TurboSuite.App` | Entry point, `SettingsCommand`, ViewModels, Views |
| `TurboSuite.Shared.Constants` | `ParameterNames` — centralized custom Revit parameter name strings |
| `TurboSuite.Shared.Converters` | WPF value converters shared across windowed commands |
| `TurboSuite.Shared.Filters` | `FixtureSelectionFilter`, `LightingFixtureTagFilter` |
| `TurboSuite.Shared.Helpers` | `GeometryHelper`, `ParameterHelper`, `NaturalStringComparer`, `FileLockHelper` |
| `TurboSuite.Shared.Models` | `WallLocalCoordinateSystem`, `FamilyNameSettings`, `CadRoomSourceSettings`, `GeneralSettings` |
| `TurboSuite.Shared.Services` | `DataStorageHelper`, `SpaceRoomFinderService` (runtime room detection — reads project-owned Spaces, not architect Rooms), `LinkedRoomFinderService` (BAND_ROOM over architect Rooms — now only seeds Space *names*), `UpdateService`, settings storage/cache services |
| `TurboSuite.Shared.Styles` | Shared WPF ResourceDictionary styles |
| `TurboSuite.Shared.ViewModels` | `ViewModelBase`, `RelayCommand` |
| `TurboSuite.Name` | TurboName — **modeless** 2D job-setup window: linked-CAD layer list (visibility, line graphics, hide-by-picking), click-to-tag layer roles, region generation, room-name/ceiling-height assignment. Scoping matcher is `Core/Name/CadLinkScope` (unit-tested). **Both shims enable `UseWindowsForms` (native `ColorDialog`) with the `View`/`Color` WinForms global usings suppressed to avoid collisions.** |
| `TurboSuite.Driver` | TurboDriver (deploys power supplies, modal) + TurboRPS (staleness dashboard + batch in-place driver-type corrector, MVVM modeless). Defer-circuit state is stored in ExtensibleStorage **on the circuit element** — a per-element entity, not the doc-singleton pattern used elsewhere |
| `TurboSuite.Bubble` | TurboBubble — switchleg tags and wires |
| `TurboSuite.Tag` | TurboTag — auto-places lighting fixture type tags |
| `TurboSuite.Wire` | TurboWire — circuit creation and wire routing |
| `TurboSuite.Zones` | TurboZones — load names and panel breakdown (MVVM, modeless) |
| `TurboSuite.Number` | TurboNumber — circuit numbers, keypads, power supply Switch IDs (MVVM, modeless) |
| `TurboSuite.Compact` | TurboCompact — family document cleanup |
| `TurboSuite.Docs` | TurboDocs — tabbed document generation: fixture schedule PDF, cut sheet PDF merging, control BOM PDF, load schedule PDF, panel schedule PDF, and cover/notes PDF (MVVM) |
| `TurboSuite.Tab` | TurboTab — document tab coloring (AvalonDock visual tree manipulation) |
| `TurboSuite.Mask` | TurboMask — masking region + per-fixture annotation stamps + detail-line overlays of connected wires. Real wires stay connected/hidden under the mask — overlays are view-only stand-ins, never a delete-recreate, so connectivity is untouched |
| `TurboSuite.Setup` | TurboSetup — landing menu routing to (a) **Project Setup**: copy levels from the linked arch model, create Floor/RCP views with firm templates, configure RVT link display (**3D RVT-linked only**; the link-graphics path is Revit 2025-only — 2024 does levels/views/templates and leaves links manual); and (b) **Name Spaces from Rooms** (`SpaceNamingService`): seed Space names from the architect Rooms (BAND_ROOM), blank-only by default with a force re-pull |
| `TurboSuite.Dmx` | TurboDMX — DMX-controlled RGBW LED tape/fixture automation (decoder/driver packing, addressing, one-line, wire legend, Control-Zone view overlay). **Gated behind `ExperimentalCommandsEnabled`; feature-complete + live-tested, end-of-project polish only** — gets its own README when it ships. Pure engine + VMs in `Core/Dmx/` (unit-tested in `Tests/`); Revit-coupled half in `Shim/Dmx/`, loop-centric modeless window (`DmxMainViewModel`). State persists in one JSON-backed ExtensibleStorage schema (`DmxStorageService`). Design rationale + domain vocabulary live **in the code**: start at the canonical containment-ladder / overloaded-word glossary on `DmxSolver` (`Core/Dmx/DmxSolver.cs`), then the per-file doc-comments — there are no separate DMX design docs |
| `TurboSuite.Snoop` | TurboSnoop — read-only "which VG checkbox do I uncheck?" reporter for linked arch families. Deliberately **read-only, no Apply** — no API can flip a per-link VG checkbox, so it names the box and the user unchecks it |
| `TurboSuite.Schedule` | TurboSchedule — page-per-Type-Mark form-view spec editor for lighting fixtures and drivers, unifying the two native spec schedules. Modeless; data-driven `FieldDef` roster, `ScheduleTypeCollector` reconciles each field across all symbols sharing a Type Mark |
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
- Custom parameters by name: "Switch ID", "Scale Factor", "Linear Length", "Linear Power", "Power", "Sub-Driver Power", "Derating Factor" (TurboDriver: Percentage — applied only to the packing ceiling, never the sub-driver-count validity math), "Dimming Protocol" (the module-type signal for TurboZones/TurboDocs — resolved via `Core/Zones/Services/DimmingModuleResolver.cs`, whose map is **not** the identity: MLV → ELV module, WIFI rides no module, DMX is owned by a subsystem that counts its own hardware — TurboDMX, via `IControlSubsystemDemandProvider` — and DALI is still benched as not-yet-supported), "Voltage", "Maximum Fixtures" (TurboDriver: `0` = no limit), "Remote Power Supply", "Load Classification Abbreviation" (**no longer read by TurboSuite** — the connector-level value it used to source module type from; the constant remains for native-Revit use), "Load Classification", "Circuit Naming", "Circuit Prefix", "Circuit Prefix Separator", "Orientation", "Angle", "Two Gang", "Catalog Number1"–"Catalog Number6" and "Catalog Qty1"–"Catalog Qty6" (TurboDocs Counts: a length-token + cut-list/qty mini-grammar — semantics and edge cases are pinned by `Core/Docs/Services/CatalogLengthTokenResolver.cs` and its oracle tests; read those, don't reconstruct the rules), "Data Sheet URL", "Manufacturer". **Access via `TurboSuite.Shared.Constants.ParameterNames` — do NOT pass string literals to `LookupParameter`.**
- **IMPORTANT**: Room name must be read via `room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()` — `room.Name` returns "Number Name" format.

### API Limitations

- **Command return value rolls back saves**: Revit DISCARDS a command's committed changes if `Execute` returns `Result.Cancelled`/`Failed`. A command that writes ExtensibleStorage (or any element) synchronously in `Execute` must return `Result.Succeeded`, or the save is silently rolled back. (Bit TurboName, since gone modeless; modeless commands saving via `ExternalEvent` are unaffected.) The discard is not always a bug — for commands that mutate *elements* it is often the atomicity you want. TurboDriver's circuit-lost-during-split branch returns `Failed` **deliberately**, because the split has already deleted the user's original fixture and rolling back is what restores it. Decide per branch: does persisting the partial work leave a state the user can act on, or debris they must clean up? **Verified by TurboSpike probe (Revit 2025):** committed work survives `Succeeded`, is discarded on `Failed` and on `Cancelled`, and — the non-obvious one — **is discarded even when committed inside an `Assimilate`d `TransactionGroup`**. Grouping buys no durability here; the return value is the only lever, so don't reach for a `TransactionGroup` expecting one. What `Assimilate` *is* for is the **undo stack**: it collapses several committed transactions into one entry, which is the only way to keep a "one Ctrl+Z puts it back" promise when the operation has to commit more than once (TurboName's auto-generate commits the regions, then `NudgeImportGraphics` commits two more to force the post-commit CAD regen — un-grouped, Ctrl+Z just toggled a pin).
- **Wire end display**: An `ElectricalWire` end vertex is pinned to the connector center (Revit re-inserts it there if moved); whether Revit visually clips the wire at the family boundary vs. draws it to center (a "tail") is a display-only decision made during **post-commit regeneration**, not controllable via the points passed to `ElectricalWire.Create`. Wire each connection in its **own committed transaction** (like the manual workflow) — batching wires in one shared transaction leaves the *last* wire's terminal end unclipped, drawn into the final element. See `DeploymentExecutor` wiring loop and the `WireCreationService` switch/sconce nudge.
- **Switch Systems** (`OST_SwitchSystem`) cannot be created or modified via the public API. Workaround: TurboDriver sets "Switch ID" parameter; users create switch systems manually. A device on a switch system reports an extra logical (`DomainUndefined`, no-origin) connector — so `ConnectorManager` count > the family's defined connectors; copies don't carry switch-system membership.
- **`PanelScheduleView.IsSlotGrouped`** is read-only — no `GroupCircuits`/`UngroupCircuits` API exists.
- **Light Group** writes require a `Transaction` — calling outside one crashes Revit (hard crash, not exception). Groups are not elements and cannot be found via `FilteredElementCollector`.
- **TextNote rotation**: `TextNote.Create` auto-orients text to be readable in the active view at orthogonal Project North angles (0°, ±90°, 180°). Manually rotating by `-ProjectPosition.Angle` at these angles doubles the rotation. Only apply rotation correction for non-orthogonal angles.
- **View-scoped collectors ignore the crop box**: `FilteredElementCollector(doc, viewId)` filters on view *ownership* and visibility/graphics, **not** the crop region — it returns elements cropped out of sight. Wherever the crop box carries meaning (TurboName: which floor of a stacked multi-floor DWG the user is on), any code that *acts* on the collected set must re-apply the crop itself, or it will operate on things the user cannot see. The dangerous shape is a read and a write that disagree: TurboName's auto-generate clipped its *generation* to the crop but collected the regions to *clear* view-wide, so clearing from one floor deleted another and never rebuilt it. See `Shim/Name/Services/CropScope.cs` — one crop object shared by both sides.
- **Drafting-view wipe**: `FilteredElementCollector(doc, viewId)` (elements *visible in* a view) returns the drafting view's **own element** (a categoryless base `Element`) alongside the content — deleting that set deletes the view itself, invalidating the wrapper (`InvalidObjectException` on the next use). To clear a program-owned view for wipe-and-redraw, delete only the kinds you draw (`e is CurveElement || e is TextNote || e is FamilyInstance` — `AnnotationSymbol` subclasses `FamilyInstance`). `OwnedByView` comes back **empty** for drafting-view detail/annotation content, so it's not a usable substitute. See `DmxOneLineService.WipeView`.
- **`NewFamilyInstance` ignores the placement Z for level-based families**: for a level-based family (e.g. the driver power supplies), `NewFamilyInstance` does **not** reliably set elevation from the point's Z — it inherits the family's **sticky "Elevation from Level" default** from the last interactive placement, so the first placement in a fresh session can land wildly off (verified in-model: a driver 1356' in the sky). Use the level-taking overload for the Level association, then **explicitly `Set(INSTANCE_ELEVATION_PARAM)`** to pin elevation authoritatively. And build any absolute placement elevation from **`Level.ProjectElevation`** (internal-origin, the frame every `LocationPoint.Z` uses), **not `Level.Elevation`** — a survey/relocated *elevation base* inflates `.Elevation` away from where geometry actually sits (the two are equal in an un-relocated project). Also note `FamilyInstance.Host` reads **null** for a correctly level-associated instance — the Level, not `Host`, is the thing to check. See `DeploymentExecutor.GetDisplayElevation` / `DeploymentService.SetElevationFromLevel`.

### WPF Patterns

- Modal `ShowDialog()` blocks Revit UI. Pattern: store target view on ViewModel, close dialog, call `uidoc.RequestViewChange(view)` after return.
- **Modeless pattern** (TurboNumber, TurboZones): `window.Show()` with `IExternalEventHandler` for all Revit API calls. ViewModels queue typed `RevitApiRequest` objects, call `ExternalEvent.Raise()`, and receive results via completion callbacks dispatched to the WPF thread. Chain sequential requests in callbacks — never raise two events simultaneously (second is silently dropped). For auto-save sites that fire on many UI events (e.g. TurboZones panel breakdown), coalesce raises via a `_savePending`/`_saveDirty` pair and re-raise from the completion callback when dirty, so the latest snapshot always lands.
- **Buttons go stale after a Revit-owned dialog**: `RelayCommand.CanExecuteChanged` subscribes only to `CommandManager.RequerySuggested`, which WPF raises off **its own** input/focus events. So when a modeless flow ends while a Revit window has focus — dismissing a `TaskDialog`, finishing a pick loop — nothing re-queries `CanExecute` and every gated button stays greyed out until the user clicks back into the WPF window or the Revit view. Whenever a property that gates `CanExecute` is set from an external-event completion, call `CommandManager.InvalidateRequerySuggested()` in its setter (see TurboName's `IsPicking`).
- **WinForms common dialogs re-position themselves**: `ColorDialog`/`FontDialog` etc. have no position API, and `CommonDialog.HookProc` handles `WM_INITDIALOG` by calling `MoveToScreenCenter` — parking the dialog at one **third** of the working area's height on whichever monitor holds the *mouse*. Passing an owner to `ShowDialog` does **not** fix this (the hook runs last and overwrites it), though it is still worth doing so the dialog can't fall behind its opener. To place one, subclass it and override `HookProc`: call `base`, then `SetWindowPos` on `WM_INITDIALOG` — it fires at final size but before paint, so there's no visible jump. Use `GetWindowRect` (device pixels) for the anchor, not WPF's DIP-based `Left`/`Top`, and clamp to the monitor work area or a dialog taller than its anchor can push OK off-screen. See `AnchoredColorDialog` in `Shim/Name/Views/LineGraphicsDialog.xaml.cs`.
- **`TaskDialog` eats `&`**: command-link and button text goes through Win32's mnemonic parser, so a bare `&` is swallowed as an accelerator prefix ("Clear all & regenerate" renders as "Clear all  regenerate"). Escape it as `&&` or, better, write the word "and" — the escape reads like a typo and gets "corrected" back.
- **Modeless doc-close guard**: a modeless window opened against a document holds live references to it — if the user closes that project while the window is open, the next interaction (or just closing it) hard-crashes Revit. Every modeless command registers its window with `ModelessWindowGuard.Register(doc, window, forceClose)` (hooked once in `OnStartup`), which force-closes it on `ControlledApplication.DocumentClosing`. **Match documents by `PathName`, not reference** — Revit hands back a *different* `Document` wrapper in the event than at command time, so `ReferenceEquals` never matches. `forceClose` must skip any doc-touching teardown a normal close defers (TurboDMX skips its active-view override revert).
- `DataGrid.SelectedItems` cannot be bound in XAML. Use code-behind `SelectionChanged` handler. Do not set `SelectedRow` from within `SetSelectedRows` — causes feedback loop clearing multi-selection.
- **TurboTab**: Walks Revit's AvalonDock visual tree via `MainWindowHandle` to color document tabs. Caches original `TabItem.Style` before modification and restores on toggle-off — never use `ClearValue(StyleProperty)`.

## Dependencies

- `RevitAPI.dll`, `RevitAPIUI.dll`, `Xceed.Wpf.AvalonDock.dll` (from the matching Revit 2024/2025 install, per shim)
- `ACadSharp` (NuGet) — DWG/DXF reading for TurboName
- `PdfSharpCore` (NuGet) — PDF operations for TurboDocs
- .NET 8.0-windows (Revit 2025 shim) / .NET Framework 4.8 (Revit 2024 shim) / Core multi-targets both / WPF
