# TurboSuite

A unified Autodesk Revit 2025 add-in for electrical and lighting automation. Ten commands plus a Settings dialog consolidated into a single `TurboSuite.dll` targeting .NET 8.0-windows (x64).

## Installation

### First-Time Setup

1. Navigate to the TurboSuite network share folder
2. Run `TurboSuiteInstaller.exe`
3. Click **Install** — the installer copies all required files to the correct locations and configures auto-update
4. Launch Revit 2025 — the TurboSuite ribbon tab will appear automatically

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
| [TurboDocs](Docs/README.md) | Generate fixture/RPS schedules, load/panel schedules, cut sheets, and cover/notes PDFs |

## Supported Workflows

All commands work in both:
- **3D Model** — Hosted families with 3D geometry in plan/RCP views
- **2D Drafting** — Unhosted families placed over linked CAD in floor plan views

## Revit Project Dependencies

TurboSuite expects certain families, parameters, and annotation types to be loaded in the Revit project. See each command's README for specific requirements. The table below summarizes shared dependencies.

### Fixture Categories

| Category | Used By |
|----------|---------|
| Lighting Fixtures (`OST_LightingFixtures`) | TurboTag, TurboWire, TurboBubble, TurboDriver, TurboZones, TurboDocs |
| Lighting Devices (`OST_LightingDevices`) | TurboTag, TurboDriver, TurboNumber, TurboZones, TurboDocs |
| Electrical Fixtures (`OST_ElectricalFixtures`) | TurboWire, TurboBubble, TurboZones |
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

- RevitAPI.dll and RevitAPIUI.dll (Revit 2025)
- Xceed.Wpf.AvalonDock.dll (ships with Revit 2025) — used by TurboTab for document tab coloring
- [ACadSharp](https://github.com/DomCR/ACadSharp) (NuGet) — .NET library for reading AutoCAD DWG/DXF files without requiring an AutoCAD installation
- [PdfSharpCore](https://github.com/ststeiger/PdfSharpCore) (NuGet) — PDF generation, reading, stamping, and merging. Used by TurboDocs.
- [ClosedXML](https://github.com/ClosedXML/ClosedXML) (NuGet) — .xlsx workbook generation. Used by TurboCounts.
- .NET 8.0-windows / WPF

## Acknowledgments

- [pyRevit](https://github.com/pyrevitlabs/pyRevit) — TurboTab's document tab coloring was inspired by pyRevit's tab coloring. pyRevit is developed by [Ehsan Iran-Nejad](https://github.com/eirannejad) and contributors under the GNU GPL v3 license.
