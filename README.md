# TurboSuite

A unified Autodesk Revit add-in for electrical and lighting automation, supporting **Revit 2024, 2025, and 2026**, built for electrical/lighting designers working on luxury architectural lighting projects. Sixteen commands plus a Settings dialog, shipped as a per-version `TurboSuite.dll` (.NET Framework 4.8 for Revit 2024, .NET 8.0-windows for Revit 2025, .NET 10.0-windows for Revit 2026), x64.

## Installation

### First-Time Setup

1. Navigate to the TurboSuite network share folder
2. Run `TurboSuiteInstaller.exe`
3. Click **Install** — the installer detects which Revit versions (2024, 2025, 2026) are present and installs the matching channel for each, copying all required files to the correct locations and configuring auto-update
4. Launch Revit 2024, 2025, or 2026 — the TurboSuite ribbon tab will appear automatically

Revit must be closed during installation.

### Auto-Update

TurboSuite checks for updates on each Revit launch by comparing version files against the network share. If a newer version is found:

1. A notification dialog lets you **accept** or **skip** the update
2. If accepted, the update is applied automatically after you close Revit
3. Skipped updates will prompt again on next launch

If the network share is unreachable (offline, VPN disconnected), TurboSuite loads normally from local files.

### Uninstall

Run `TurboSuiteInstaller.exe` again and click **Uninstall** to remove all TurboSuite files. Revit must be closed before uninstalling.

## Ribbon Tab

The "TurboSuite" ribbon tab has four panels:

### Settings Panel

| Button | Description |
|--------|-------------|
| [Settings](Shim/App/README.md) | Configure family name settings stored in ExtensibleStorage |
| [TurboTab](Shim/Tab/README.md) | Toggle document tab coloring — colors each open tab by project for visual identification |

### Tools Panel

| Script | Shortcut | Description |
|--------|----------|-------------|
| [TurboNudge](Shim/Nudge/README.md) | `TN` | Slide a selected keypad along its wall to exactly 5" from a picked door corner — replaces the manual place-then-dimension flow (3D and 2D) |

### Commands Panel

| Button | Shortcut | Description |
|--------|----------|-------------|
| [TurboCompact](Shim/Compact/README.md) | `Ctrl+Shift+S` | Remove unused materials and compact-save the active family |
| [TurboSnoop](Shim/Snoop/README.md) | `TS` | Selection-aware: snoop your own element's host (churn/orphan risk), or pick a linked family to list the Visibility/Graphics checkboxes it draws under |
| [TurboTag](Shim/Tag/README.md) | `TT` | Batch-place type tags on selected lighting fixtures |
| [TurboWire](Shim/Wire/README.md) | `WW` | Create arc/spline wires between fixtures |
| [TurboBubble](Shim/Bubble/README.md) | `TB` | Place switchleg tag and stub wire on a fixture |
| [TurboDriver](Shim/Driver/README.md) | `TD` | Deploy power supplies for selected RPS fixtures |
| [TurboMask](Shim/Mask/README.md) | `BB` | Mask selected elements while preserving fixture footprint graphics |

### Utilities Panel

| Button | Description |
|--------|-------------|
| [TurboSetup](Shim/Setup/README.md) | Set up a new project from the linked architectural model (levels, views, link graphics), and name Spaces from the architect's Rooms |
| [TurboName](Shim/Name/README.md) | Modeless 2D job setup: linked-CAD layer show/hide + role tagging, region generation, and CAD room-name/ceiling-height assignment to filled regions |
| [TurboSchedule](Shim/Schedule/README.md) | Edit lighting fixture and driver type specs, one Type Mark per page — plus a per-project Excel round-trip for non-Revit designers |
| [TurboZones](Shim/Zones/README.md) | Manage circuit load names and visualize dimmer panel allocation |
| [TurboNumber](Shim/Number/README.md) | Manage circuit numbers, keypad and power supply Switch IDs |
| [TurboRPS](Shim/Driver/README.md) | Flag stale power-supply selections across all RPS circuits and batch-fix them in place |
| [TurboDocs](Shim/Docs/README.md) | Generate fixture/RPS schedules, load/panel schedules, cut sheets, and cover/notes PDFs |

## Supported Workflows

All commands work in both:
- **3D Model** — Hosted families with 3D geometry in plan/RCP views
- **2D Drafting** — Unhosted families placed over linked CAD in floor plan views

## Documentation

- **[Counts Cheat Sheet](https://cdltg.github.io/TurboSuite3D/)** — quick reference for authoring TurboDocs **Counts** parameters: Catalog Number length tokens (`max=` / `sizes=` / `pool=` / `min=`) and Catalog Qty modes (`N`, `1/N`, `N @type`, and the stock-cut `N @ft` / `N @in`). Served via GitHub Pages from `docs/index.html`.

## Revit Project Dependencies

TurboSuite expects certain families, parameters, and annotation types to be loaded in the Revit project. See each command's README for specific requirements. The table below summarizes shared dependencies.

### Fixture Categories

| Category | Used By |
|----------|---------|
| Lighting Fixtures (`OST_LightingFixtures`) | TurboTag, TurboWire, TurboBubble, TurboDriver, TurboZones, TurboDocs, TurboMask |
| Lighting Devices (`OST_LightingDevices`) | TurboTag, TurboDriver, TurboNumber, TurboZones, TurboDocs, TurboMask, TurboNudge |
| Electrical Fixtures (`OST_ElectricalFixtures`) | TurboWire, TurboBubble, TurboZones, TurboMask |
| Electrical Equipment (`OST_ElectricalEquipment`) | TurboWire, TurboNumber, TurboZones, TurboMask |

### Common Custom Parameters

| Parameter | On | Type | Used By |
|-----------|----|------|---------|
| `Switch ID` | Lighting Device instances | Text | TurboDriver, TurboNumber |
| `Remote Power Supply` | Lighting Fixture types | Yes/No (Integer) | TurboDriver, TurboWire, TurboBubble |
| `Power` | Lighting Device types | Double (Watts) | TurboDriver, TurboRPS |
| `Sub-Driver Power` | Lighting Device types | Double (Watts) | TurboDriver, TurboRPS, TurboTag, TurboNumber |
| `Scale Factor` | Fixture instances | Double | TurboBubble, TurboWire |
| `Dimming Protocol` | Lighting Fixture types | Text | TurboZones, TurboDocs, TurboDriver |
| `Data Sheet URL` | Lighting Fixture/Device types | Text (URL) | TurboDocs |

### Tag Families

| Family Name | Category | Used By |
|-------------|----------|---------|
| `AL_Tag_Lighting Fixture (Type)` | Lighting Fixture Tags | TurboTag |
| `AL_Tag_Lighting Fixture (Linear Length)` | Lighting Fixture Tags | TurboTag, TurboDriver |
| `AL_Tag_Lighting Fixture (Switchleg)` | Lighting Fixture Tags | TurboBubble |
| `AL_Tag_Lighting Fixture (Remote Switchleg)` | Lighting Fixture Tags | TurboBubble |
| `AL_Tag_Electrical Fixture (Switchleg)` | Electrical Fixture Tags | TurboBubble |
| `AL_Tag_Lighting Device (SwitchID)` | Lighting Device Tags | TurboTag, TurboDriver |
| `AL_Tag_Lighting Device (Keypad)` | Lighting Device Tags | TurboTag |
| `AL_Tag_Lighting Device (Switchleg)` | Lighting Device Tags | TurboDriver |

### Text Note Types

| Type Name | Used By |
|-----------|---------|
| `AL_Annotation_4.5"` | TurboName |
| `AL_Annotation_3"` | TurboName (optional — ceiling descriptions) |

### Other Requirements

- At least one **WireType** must exist in the project (TurboWire, TurboBubble, TurboDriver)
- A **Filled Region** type named `Room Region` must exist (TurboName)
- Linked DWG files with room name blocks or text layers (TurboName)

## Software Dependencies

- RevitAPI.dll and RevitAPIUI.dll (Revit 2024, 2025, or 2026, depending on the channel)
- Xceed.Wpf.AvalonDock.dll (ships with Revit) — used by TurboTab for document tab coloring
- [ACadSharp](https://github.com/DomCR/ACadSharp) (NuGet) — .NET library for reading AutoCAD DWG/DXF files without requiring an AutoCAD installation
- [PDFsharp](https://github.com/empira/PDFsharp) (NuGet) — PDF generation, reading, stamping, and merging. Used by TurboDocs.
- .NET Framework 4.8 (Revit 2024) / .NET 8.0-windows (Revit 2025) / .NET 10.0-windows (Revit 2026) / WPF

## Building from Source

```bash
dotnet build TurboSuite.sln -c Release
```

The shared add-in source lives once in `Shim/` (a Visual Studio Shared Project) and compiles into a thin per-version shim: `Revit2024/` (net48, Revit 2024 API), `Revit2025/` (net8.0-windows, Revit 2025 API), and `Revit2026/` (net10.0-windows, Revit 2026 API). Each emits `TurboSuite.dll` into its own `Addins\{year}\` folder. To build a single channel directly:

```bash
dotnet build Revit2025/TurboSuite.Revit2025.csproj -c Release   # or Revit2024/..., Revit2026/...
```

Platform target is **x64**. Requires the .NET 8.0 SDK and a local install of the matching Revit version (for `RevitAPI.dll` / `RevitAPIUI.dll` references). Post-build copies each add-in into `%APPDATA%\Autodesk\Revit\Addins\{year}\` for local testing.

## License

Released under the [GNU General Public License v3.0](LICENSE).

## Acknowledgments

- [pyRevit](https://github.com/pyrevitlabs/pyRevit) — TurboTab's document tab coloring was inspired by pyRevit's tab coloring. pyRevit is developed by [Ehsan Iran-Nejad](https://github.com/eirannejad) and contributors under the GNU GPL v3 license.
