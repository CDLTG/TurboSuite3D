# TurboSuite

A unified Autodesk Revit 2025 add-in for electrical and lighting automation. Nine commands consolidated into a single `TurboSuite.dll` targeting .NET 8.0-windows (x64).

## Installation

Build with `dotnet build TurboSuite.csproj`. The post-build target copies `TurboSuite.addin` and `TurboSuite.dll` to `%APPDATA%\Autodesk\Revit\Addins\2025\`. Revit discovers `.addin` files from that directory on startup.

## Commands

The ribbon tab has two panels:

### Commands Panel

| Button | Shortcut | Description |
|--------|----------|-------------|
| [TurboCompact](Compact/README.md) | `Ctrl+Shift+S` | Remove unused materials and compact-save the active family |
| [TurboTag](Tag/README.md) | `TT` | Batch-place type tags on selected lighting fixtures |
| [TurboWire](Wire/README.md) | `WW` | Create arc/spline wires between fixtures |
| [TurboBubble](Bubble/README.md) | `TB` | Place switchleg tag and stub wire on a fixture |
| [TurboDriver](Driver/README.md) | `TD` | Deploy power supplies for selected RPS fixtures |
| [TurboName](Name/README.md) | | Assign CAD room names and ceiling heights to filled regions |

### Utilities Panel

| Button | Description |
|--------|-------------|
| [TurboZones](Zones/README.md) | Manage circuit load names and visualize dimmer panel allocation |
| [TurboNumber](Number/README.md) | Manage circuit numbers, keypad and power supply Switch IDs |
| [TurboRPS](Driver/README.md) | Review power supply assignments across all RPS circuits |

## Supported Workflows

All commands work in both:
- **3D Model** — Hosted families with 3D geometry in plan/RCP views
- **2D Drafting** — Unhosted families placed over linked CAD in floor plan views

## Dependencies

- RevitAPI.dll and RevitAPIUI.dll (Revit 2025)
- [ACadSharp](https://github.com/DomCR/ACadSharp) (NuGet) — .NET library for reading AutoCAD DWG/DXF files without requiring an AutoCAD installation.
- .NET 8.0-windows / WPF

## Acknowledgments

- [pyRevit](https://github.com/pyrevitlabs/pyRevit) — TurboTab's document tab coloring was inspired by pyRevit's tab coloring. pyRevit is developed by [Ehsan Iran-Nejad](https://github.com/eirannejad) and contributors under the GNU GPL v3 license.
