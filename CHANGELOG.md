# Changelog

All notable changes to TurboSuite are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version numbers follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.2.0] — 2026-06-05

### Added
- Multi-version Revit support: TurboSuite now builds and ships for **Revit 2024 (.NET Framework 4.8)** alongside Revit 2025 (.NET 8) from a single shared source tree. Version-agnostic logic lives in `TurboSuite.Core`/`TurboSuite.Abstractions`; thin per-version shim projects (`Revit2024`/`Revit2025`) each emit their own `TurboSuite.dll`. Existing Revit 2025 behavior, ExtensibleStorage schemas, and parameter names are unchanged.

### Changed
- Deployment is now **per Revit version**: each version installs to `Addins\{ver}\` and isolates its auto-update state under `%LOCALAPPDATA%\TurboSuite\{ver}\`, so multiple Revit versions coexist on one machine without interfering. The network share gains per-version subfolders (`\2024\`, `\2025\`), each independently versioned and rolled back; `publish.ps1` takes a `-RevitVersion` parameter and is run once per version.
- A single combined installer auto-detects installed Revit versions and installs the matching add-in(s). **One-time migration for existing 1.1.0 users:** run the new installer's Uninstall, then Install, to move from the old flat layout to the per-version layout (see `PUBLISHING.md`).

### Fixed
- Counts: Reel/Channel Qty under-counted when stock length was fractional (e.g. 5m reels = 16.404 ft). Removed inner `CEILING` on stock length so the user-entered value is trusted as the actual usable length.

## [1.1.0] — 2026-05-29

### Added
- TurboZones: amp-aware module allocation with overload flagging — per-part-number limits (`LQSE-4A5`, `LQSE-4T5`, `LQSE-4S8`), slot-1 promotion, FFD bin-packing fallback when sequential order overloads, red overload rendering in Panel Breakdown and Panel Schedule PDF.
- TurboNumber: persist Room Order click-order badges across sessions.
- Counts: `pool=` length-token modifier for cross-instance offcut reuse (18" min reusable offcut).

### Changed
- TurboZones: default Relay loads to the `LQSE-4T5` 0-10V module; toggle for dedicated `LQSE-4S8`.
- TurboMask: extend supported categories, re-run safety, tag draw order, grouping (still gated behind `ExperimentalCommandsEnabled`).
- Counts: widen EL rep-mismatch flag to include CS-channel SKUs.
- Counts: preserve pricer cell highlights across Worksheet regeneration.

### Removed
- Counts: drop Slot column from the hidden Waste audit sheet.

### Fixed
- Counts: Worksheet freeze-column pane and Changes-sheet Type Mark sort.
- TurboNumber: drag-down index bug in Room Order.

### Infrastructure
- `publish.ps1`: pre-flight check that CHANGELOG.md has an entry for the publishing version.

## [1.0.3] — 2026-05-27

### Changed
- Counts: revamp length tokens — unit-suffix format (`{xx"}`, `{xxIN}`, `{xx'}`, `{xxFT}`, `{xx'-xx"}`, `{xxFT-xxIN}`) with feet-only variants that truncate (integer divide by 12).

### Fixed
- Counts: `sizes=` length-token modifier bug in stock-stick coverage.

## [1.0.2] — 2026-05-27

### Changed
- Counts: simplify length token syntax — `{xx}` replaces the verbose `{L:in}` form.

### Infrastructure
- `publish.ps1`: tag the release commit with `v<version>` and push to origin.

## [1.0.1] — 2026-05-26

### Infrastructure
- `publish.ps1`: add UTF-8 BOM and CRLF line endings to fix a PowerShell 5.1 parse error.
- `PUBLISHING.md`: add post-1.0.0 SemVer versioning guidance.

## [1.0.0] — 2026-05-26

First public release. Eleven Revit 2025 commands plus a Settings dialog, shipped as a single `TurboSuite.dll` add-in with auto-update via a network share.

### Added

**Commands panel**
- TurboCompact (`Ctrl+Shift+S`) — remove unused materials and compact-save the active family.
- TurboTag (`TT`) — batch-place type tags on selected lighting fixtures, devices, and electrical fixtures.
- TurboWire (`WW`) — create electrical circuits and route arc/spline wires between fixtures.
- TurboBubble (`TB`) — place switchleg tag and stub wire on a fixture.
- TurboDriver (`TD`) — deploy power supplies for selected RPS fixtures.

**Utilities panel**
- TurboName — assign CAD room names and ceiling heights to filled regions, with optional region auto-generation from CAD wall lines.
- TurboZones — manage circuit load names and visualize dimmer-panel load distribution.
- TurboNumber — modeless window for managing circuit numbers, keypad Switch IDs, and power-supply Switch IDs.
- TurboRPS — review power supply assignments across all RPS circuits.
- TurboDocs — tabbed document generation: fixture/RPS schedules, cut-sheet PDF merging, control BOM PDF, load schedule PDF, panel schedule PDF, cover/notes PDF.

**Settings panel**
- Settings — configure family-name / CAD-source / general settings stored in project ExtensibleStorage.
- TurboTab — toggle document tab coloring per project.

**Workflow support**
- All commands support both 3D hosted-family workflows and 2D drafting (unhosted families placed over linked CAD).

### Infrastructure
- Auto-update on Revit launch via `%LOCALAPPDATA%\TurboSuite\config.json` → network share → staged install applied by `TurboSuiteUpdater.exe` after Revit exits.
- Standalone `TurboSuiteInstaller.exe` for first-time install and uninstall.
- .NET 8.0-windows, x64. Requires Revit 2025.

### Known limitations
- Switch Systems (`OST_SwitchSystem`) cannot be created or modified via the public Revit API. TurboDriver sets the `Switch ID` parameter; users create switch systems manually.
- `PanelScheduleView.IsSlotGrouped` is read-only — no `GroupCircuits`/`UngroupCircuits` API exists.
- `SixLabors.ImageSharp 1.0.4` (transitive via `PdfSharpCore`) carries known unfixed CVEs. Accepted for 1.0.0 — only user-chosen logo files are processed. Upgrade tracked post-1.0.
- Single Revit version. 1.0.0 targets Revit 2025 only. Multi-version support (2024/2026) is post-1.0.

### Install
See [README.md](README.md#installation).

### Security
See [SECURITY.md](SECURITY.md).

[1.1.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.1.0
[1.0.3]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.3
[1.0.2]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.2
[1.0.1]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.1
[1.0.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.0
