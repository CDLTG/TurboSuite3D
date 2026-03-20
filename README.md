# TurboSuite

A unified Autodesk Revit 2025 add-in for electrical and lighting automation. Ten commands plus a Settings dialog consolidated into a single `TurboSuite.dll` targeting .NET 8.0-windows (x64).

## Installation

### First-Time Setup

1. Copy `TurboSuite.addin` to `%APPDATA%\Autodesk\Revit\Addins\2025\`
2. Copy `TurboSuite.dll` and all supporting DLLs to `%APPDATA%\Autodesk\Revit\Addins\2025\TurboSuite\`
3. Revit discovers `.addin` files from that directory on startup.

### Auto-Update

TurboSuite checks for updates on each Revit launch by comparing version files against a shared server. If a newer version is found:

1. Update files are staged to `%LOCALAPPDATA%\TurboSuite\Staging\`
2. A notification dialog lets you **accept** or **skip** the update
3. If accepted, the update is applied automatically after you close Revit

If the server is unreachable (offline, VPN disconnected), TurboSuite loads normally from local files. Skipped updates remain staged and you will be prompted again on next launch.

### Developer Build

Build with `dotnet build TurboSuite.sln`. The post-build target copies output to the Revit add-ins folder and places `TurboSuiteUpdater.exe` in `%LOCALAPPDATA%\TurboSuite\`.

## Ribbon Tab

The "TurboSuite" ribbon tab has three panels:

### Settings Panel

| Button | Description |
|--------|-------------|
| [Settings](App/README.md) | Configure family name settings stored in ExtensibleStorage |
| [TurboTab](Tab/README.md) | Toggle document tab coloring — colors each open tab by project for visual identification |

### Commands Panel

| Button | Shortcut | Description |
|--------|----------|-------------|
| [TurboCompact](Compact/README.md) | `Ctrl+Shift+S` | Remove unused materials and compact-save the active family |
| [TurboTag](Tag/README.md) | `TT` | Batch-place type tags on selected lighting fixtures |
| [TurboWire](Wire/README.md) | `WW` | Create arc/spline wires between fixtures |
| [TurboBubble](Bubble/README.md) | `TB` | Place switchleg tag and stub wire on a fixture |
| [TurboDriver](Driver/README.md) | `TD` | Deploy power supplies for selected RPS fixtures |

### Utilities Panel

| Button | Description |
|--------|-------------|
| [TurboName](Name/README.md) | Assign CAD room names and ceiling heights to filled regions |
| [TurboZones](Zones/README.md) | Manage circuit load names and visualize dimmer panel allocation |
| [TurboNumber](Number/README.md) | Manage circuit numbers, keypad and power supply Switch IDs |
| [TurboRPS](Driver/README.md) | Review power supply assignments across all RPS circuits |

## Supported Workflows

All commands work in both:
- **3D Model** — Hosted families with 3D geometry in plan/RCP views
- **2D Drafting** — Unhosted families placed over linked CAD in floor plan views

## Revit Project Dependencies

TurboSuite expects certain families, parameters, and annotation types to be loaded in the Revit project. See each command's README for specific requirements. The table below summarizes shared dependencies.

### Fixture Categories

| Category | Used By |
|----------|---------|
| Lighting Fixtures (`OST_LightingFixtures`) | TurboTag, TurboWire, TurboBubble, TurboDriver, TurboZones |
| Electrical Fixtures (`OST_ElectricalFixtures`) | TurboWire, TurboBubble, TurboZones |
| Lighting Devices (`OST_LightingDevices`) | TurboTag, TurboDriver, TurboNumber, TurboZones |
| Electrical Equipment (`OST_ElectricalEquipment`) | TurboWire, TurboNumber, TurboZones |

### Common Custom Parameters

| Parameter | On | Type | Used By |
|-----------|----|------|---------|
| `Switch ID` | Lighting Device instances | Text | TurboDriver, TurboNumber |
| `Remote Power Supply` | Lighting Fixture types | Yes/No (Integer) | TurboDriver, TurboWire, TurboBubble |
| `Power` | Lighting Device types | Double (Watts) | TurboDriver, TurboRPS |
| `Sub-Driver Power` | Lighting Device types | Double (Watts) | TurboDriver, TurboRPS, TurboTag, TurboNumber |
| `Scale Factor` | Fixture instances | Double | TurboBubble, TurboWire |
| `Load Classification Abbreviation` | Electrical Circuits | Text | TurboZones, TurboDriver |

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

- RevitAPI.dll and RevitAPIUI.dll (Revit 2025)
- Xceed.Wpf.AvalonDock.dll (ships with Revit 2025) — used by TurboTab for document tab coloring
- [ACadSharp](https://github.com/DomCR/ACadSharp) (NuGet) — .NET library for reading AutoCAD DWG/DXF files without requiring an AutoCAD installation
- .NET 8.0-windows / WPF

## Acknowledgments

- [pyRevit](https://github.com/pyrevitlabs/pyRevit) — TurboTab's document tab coloring was inspired by pyRevit's tab coloring. pyRevit is developed by [Ehsan Iran-Nejad](https://github.com/eirannejad) and contributors under the GNU GPL v3 license.
