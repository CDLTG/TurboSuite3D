# Changelog

All notable changes to TurboSuite are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version numbers follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] — 2026-05-26

First public release. Eleven Revit 2025 commands plus a Settings dialog, shipped as a single
`TurboSuite.dll` add-in with auto-update via a network share.

### What's in the box

**Commands panel**

- **TurboCompact** (`Ctrl+Shift+S`) — Remove unused materials and compact-save the active family.
- **TurboTag** (`TT`) — Batch-place type tags on selected lighting fixtures, devices, and electrical fixtures.
- **TurboWire** (`WW`) — Create electrical circuits and route arc/spline wires between fixtures.
- **TurboBubble** (`TB`) — Place switchleg tag and stub wire on a fixture.
- **TurboDriver** (`TD`) — Deploy power supplies for selected RPS fixtures.

**Utilities panel**

- **TurboName** — Assign CAD room names and ceiling heights to filled regions, with optional region auto-generation from CAD wall lines.
- **TurboZones** — Manage circuit load names and visualize dimmer-panel load distribution.
- **TurboNumber** — Modeless window for managing circuit numbers, keypad Switch IDs, and power-supply Switch IDs.
- **TurboRPS** — Review power supply assignments across all RPS circuits.
- **TurboDocs** — Tabbed document generation: fixture/RPS schedules, cut sheet PDF merging, control BOM PDF, load schedule PDF, panel schedule PDF, and cover/notes PDF.

**Settings panel**

- **Settings** — Configure family-name/CAD-source/general settings stored in project ExtensibleStorage.
- **TurboTab** — Toggle document tab coloring (by project, for visual identification).

### Workflows

- All commands support both **3D hosted-family** workflows and **2D drafting** (unhosted families placed over linked CAD).

### Infrastructure

- Auto-update on Revit launch via `%LOCALAPPDATA%\TurboSuite\config.json` → network share → staged install applied by `TurboSuiteUpdater.exe` after Revit exits.
- Standalone `TurboSuiteInstaller.exe` for first-time install and uninstall.
- .NET 8.0-windows, x64. Requires Revit 2025.

### Known limitations

- **Switch Systems** (`OST_SwitchSystem`) cannot be created or modified via the public Revit API. TurboDriver sets the "Switch ID" parameter; users create switch systems manually.
- **`PanelScheduleView.IsSlotGrouped`** is read-only — no `GroupCircuits`/`UngroupCircuits` API exists.
- **`SixLabors.ImageSharp 1.0.4`** (transitive via `PdfSharpCore`) carries known unfixed CVEs. Accepted for v1.0.0 — only user-chosen logo files are processed. Upgrade tracked post-1.0.
- **Single Revit version.** v1.0.0 targets Revit 2025 only. Multi-version support (2024/2026) is post-1.0.

### Install

See [README.md](README.md#installation).

### Security

See [SECURITY.md](SECURITY.md).

[1.0.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.0
