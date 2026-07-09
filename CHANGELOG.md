# Changelog

All notable changes to TurboSuite are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version numbers follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.4.0] — 2026-07-09

### Added
- TurboSetup: graduated from experimental to a shipped command — copies levels from the linked arch model, creates Floor/RCP views with firm templates, and configures RVT link display (link-graphics path is Revit 2025-only; 2024 sets up levels/views/templates and leaves links manual).
- TurboDocs: Power Supplies driver breakdown output.
- TurboWire: circuit Room Override, single mixed-category circuits, and a voltage-mismatch guard.
- TurboSchedule: DMX type params (Channels, Bundle Size, Amps Per Channel).
- TurboRPS: recognizes DMX-decoder-controlled circuits and excludes decoders from the driver candidate pool, flagging them green as "DMX — not driver-managed".

### Changed
- TurboSetup: sweep Toposolid visibility off across all AL_ lighting templates.
- TurboMask: filter-aware wire overlays, raise selected detail lines, and a dependent-view guard.
- TurboSchedule: shared blue header bar and content background.
- Shared: SemiBold GroupBox headers across Docs, Settings, and Name; consolidated right-angle checks into `ViewOrientationHelper`.
- Deps: batch dependabot bumps (ACadSharp, System.Memory, test tooling).

### Fixed
- TurboMask: orient the masking region to the selection and fix annotation stamps in rotated views.
- TurboName: align room labels to the view crop rotation.
- TurboBubble: keep the switchleg bubble offset consistent for rotated fixtures.
- TurboDriver: stack power supplies relative to view crop rotation.

## [1.3.0] — 2026-06-30

### Added
- TurboSnoop: new read-only reporter that names the Visibility/Graphics Category → Subcategory checkboxes a linked arch family's geometry draws under, so you know which box to uncheck.
- TurboMask: new command for masking regions + per-fixture annotation stamps, with view-only detail-line overlays of the masked devices' wires drawn above the mask (real wires stay connected/hidden underneath).
- TurboSchedule: new page-per-Type-Mark form-view spec editor for lighting fixtures and drivers, unifying the two native spec schedules — with live Notes character count, Yes/No checkboxes, and clickable URL fields.
- TurboNumber: live name filter for the Keypads Room Order sidebar.

### Changed
- Ribbon: dropped the "Turbo" prefix from button labels (one line each).
- Settings: reports the loaded assembly version instead of a stale `version.txt`.
- Icons: refreshed all ribbon icons.

## [1.2.2] — 2026-06-12

### Fixed
- Auto-update: a cold network share at Revit launch (SMB connect/auth can take ~30–60 s on first touch) made the version check time out at 3 s and silently skip the update for the whole session. The check now waits up to 30 s per attempt and retries up to 3× before giving up, distinguishing a transient miss from "already current". All waiting stays off the Revit UI thread.

## [1.2.1] — 2026-06-12

### Added
- TurboRPS: rebuilt as a modeless staleness dashboard with an in-place driver-type corrector for batch-fixing stale circuit configs.
- TurboRPS: Switch ID (Number) column with search, and right-click **Defer circuit** to flag an intentionally-"wrong" config (resurfaces as REVIEW if the snapshotted config later drifts).
- TurboName: in-app CAD Room Source discovery in Settings — no AutoCAD required.
- TurboName: Pick from view shows the picked room's `value=tag` attribute pairs.
- TurboNumber: suffix co-circuit power supplies a/b/c by plan position.
- Counts: `N @ft`/`N @in` Length mode for Catalog Qty (stock-cut quantities).
- Counts: explode length-token catalogs into per-length rows on the Bid Compare sheet.

### Changed
- Counts: merged the Calc column into Catalog Qty.
- Counts: flow Qty Override into the Bid Compare baseline; Worksheet Δ stays raw-vs-raw.
- Unpinned ClosedXML to 0.105.0 and rewrote Counts IIFE LAMBDAs as LET.
- Bumped ACadSharp to 3.6.12.

### Fixed
- TurboDriver: snap the first driver's Z to the view's display plane so annotation-only supplies stay visible.
- TurboZones: persist room overrides per-circuit and fix region bleed.
- TurboZones: sort circuits naturally in panel allocation (E1, E2, … E10).
- Counts: fix Bid Compare ΔQty for CatalogQty slots.

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

[1.4.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.4.0
[1.3.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.3.0
[1.2.2]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.2.2
[1.2.1]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.2.1
[1.2.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.2.0
[1.1.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.1.0
[1.0.3]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.3
[1.0.2]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.2
[1.0.1]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.1
[1.0.0]: https://github.com/CDLTG/TurboSuite3D/releases/tag/v1.0.0
