# TurboSuite

A unified Autodesk Revit 2025 add-in for electrical and lighting automation. Seven commands consolidated into a single `TurboSuite.dll` targeting .NET 8.0-windows (x64).

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

### Utilities Panel

| Button | Description |
|--------|-------------|
| [TurboZones](Zones/README.md) | Manage circuit load names and visualize dimmer panel allocation |
| [TurboNumber](Number/README.md) | Manage circuit numbers, keypad and power supply Switch IDs |
| [TurboDriver](Driver/README.md) | Assign driver family types to RPS circuits via bin-packing |

## Supported Workflows

All commands work in both:
- **3D Model** — Hosted families with 3D geometry in plan/RCP views
- **2D Drafting** — Unhosted families placed over linked CAD in floor plan views

## Dependencies

- RevitAPI.dll and RevitAPIUI.dll (Revit 2025)
- .NET 8.0-windows / WPF (no external NuGet packages)
