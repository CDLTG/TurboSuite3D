# TurboDocs

Tabbed document generation utility. Seven output tabs: **Cover** (cover page and general/control notes PDF), **Schedule** (fixture schedule PDF), **Power Supplies** (RPS schedule, switch ID lookup table, and driver breakdown PDF), **Cut Sheets** (spec sheet PDF merging), **Control BOM** (control system bill of materials PDF), **Load Schedule** (electrical circuit load schedule PDF), and **Panel Schedule** (dimmer panel breakdown PDF). A **Settings** tab configures shared company info and page options.

## Schedule Tab

Generates a fixture schedule PDF from lighting fixture type parameters.

### Layout

Each fixture entry is a card-style block:
- **Type Mark** in a bordered box on the left (font auto-shrinks for long marks)
- **Catalog Numbers** (pipe-separated, bold) on the first line
- **Manufacturer**, **Description**, **Description2** on lines 2–4
- Three spec sections to the right, each compacting independently (empty values shift remaining values up):
  - **Mechanical**: Finish, Listings, Mounting
  - **Electrical**: Dimming, Wattage, Voltage
  - **Photometric**: Lumens, CCT, CRI
- **Schedule Notes** below (en-dash bulleted, with word wrapping)

Spec sections are dynamically positioned — column gaps adapt to the widest content across all entries while maintaining vertical alignment. Overflow handling: Type Mark and Catalog Numbers auto-shrink font size; Manufacturer and Descriptions shrink to stay within capped width so spec columns fit on the page.

Entries are grouped by **Classification** (alphabetically, empty classification at bottom) with a header and rule line per group.

### Variant Collapsing

Multiple families that share a Type Mark (e.g. `Tape` / `Tape (Arc)` / `Tape (Hook)` for different geometric variants of the same spec) are collapsed to one row when every spec field matches. If any field differs, all variants stay as separate rows and are tinted amber in the grid with a tooltip — a hint to reconcile the parameters in Revit before generating the schedule.

A **Specification Notes** block is appended after all fixture entries — up to 6 numbered notes (e.g., contractor instructions, approval requirements). Notes are editable in the UI with sensible defaults and persist across sessions. Empty notes are omitted and the remaining notes renumber sequentially.

### Page Formats

- **8.5" x 28.5"** — construction document strip (default)
- **8.5" x 11"** — standard letter

Page breaks never split a fixture entry. Classification headers are kept with at least one entry. Headers (project name, subtitle, note, logo) repeat on every page. Footer (company info + page numbers) appears on letter format only — the 8.5" × 28.5" construction strip is a field reference, not a deliverable, so it ships unfooted.

### Parameter Mapping

| Schedule Field | Revit Parameter |
|---------------|-----------------|
| Classification | Classification (groups entries in PDF) |
| Type Mark | ALL_MODEL_TYPE_MARK (built-in) |
| Catalog #1–6 | Catalog Number1–6 |
| Manufacturer | ALL_MODEL_MANUFACTURER (built-in) |
| Description | ALL_MODEL_DESCRIPTION (built-in) |
| Description 2 | Description2 |
| Finish | Finish1 + Finish2 (concatenated) |
| Listings | Listings and Ratings |
| Mounting | Mounting |
| Dimming | Dimming Protocol |
| Wattage | Power (displayed as W; W/ft if Linear Power > 0; hidden if 0) |
| Voltage | Voltage (hidden if both Wattage and Lumens are 0) |
| Lumens | Lumens (displayed as lm/ft if Linear Power > 0; hidden if 0) |
| CCT | Correlated Color Temperature (CCT) |
| CRI | Color Rendering Index (CRI) |
| Notes 1–6 | Schedule Notes1–6 |
| (linear detect) | Linear Power (instance param; > 0 triggers W/ft and lm/ft display) |

Embedded line feeds (alt+0010) in parameter values are replaced with ", " for single-line display.

## Load Schedule Tab

Generates a load schedule PDF from electrical circuit parameters in a flat table format.

### Layout

Seven-column table: **Ckt | Load | Dimming | Fixtures | Qty | Driver | Watts**

- Column headers repeat on each page with a rule line underneath
- Subtle horizontal gridlines between rows
- Circuit column is centered; all others left-aligned
- Load column gets remaining page width after other columns are measured to fit content
- Load names truncated with ellipsis if they exceed available width
- Circuits named `<unnamed>` display as `<...>`
- Circuits named `Feed Through Lugs` are excluded

**Fixtures column** — smart Type Mark combining:
- All same Type Mark → show as-is
- Different Type Marks with shared alpha prefix → combine with `#` (e.g. `AS2, AS3` → `AS#`)
- Mixed prefixes → show all alphabetically (e.g. `AR3, AS2, AS3` → `AR3,AS#`)

**Qty column** — point-based fixtures sum to integer count; linear fixtures sum to total feet (e.g. `38.5'`)

**Driver column** — Switch IDs from remote power supplies (`OST_LightingDevices`), with consecutive suffix combining (e.g. `X04a,X04b,X04c,X04d` → `X04a-d`)

### Parameter Mapping

| Field | Source |
|-------|--------|
| Circuit Number | RBS_ELEC_CIRCUIT_NUMBER |
| Load Name | RBS_ELEC_CIRCUIT_NAME |
| Dimming | Load Classification Abbreviation |
| Fixtures | ALL_MODEL_TYPE_MARK (from OST_LightingFixtures + OST_ElectricalFixtures) |
| Qty | Element count or Linear Length sum |
| Driver | Switch ID (from OST_LightingDevices on circuit) |
| Watts | RBS_ELEC_APPARENT_LOAD |

## Panel Schedule Tab

Generates a dimmer panel schedule PDF from TurboZones panel breakdown data. Each panel starts on its own page so pages can be separated and distributed.

### Layout

Hierarchical structure per panel:
- **Panel header** (dark band) — panel name, part number in brackets, total panel wattage
- **Module sections** (boxed) — module number, part number, total module wattage in header; per-slot load rows underneath with empty slots marked "— spare —"

Five-column table per module: **# | Load | Ckt | Dimming | Watts**

Modules never split across pages. When a panel's modules span multiple pages, the panel header repeats with "(continued)". Panel wattage is calculated from rounded module totals to avoid rounding discrepancies.

### Data Source

Re-derives the panel breakdown by reading saved TurboZones settings (brand, panel size overrides) from ExtensibleStorage and running `PanelAllocationService.BuildPanelBreakdown`. Circuit wattage is read from `RBS_ELEC_APPARENT_LOAD`.

## Power Supplies Tab

Generates documentation for remote power supplies (`OST_LightingDevices`) with three independent output checkboxes — **RPS Schedule** (type-level specification schedule), **Lookup Table** (switch ID to circuit mapping), and **Driver Breakdown** (per-circuit sub-driver packing). Any combination merges into one PDF in that reading order.

### RPS Schedule

Same visual format as the fixture schedule but with two spec sections: **Capacity** (Power, Sub-Driver, Max Fixtures) and **Electrical** (Dimming, Voltage). Supports classification grouping, specification notes, and small/large page format.

### Lookup Table

Compact table with columns **Number | Type | Catalog Number | Load Name | Circuit**, sorted by Switch ID (numeric-aware). Letter size only. Dark header row with alternating row shading.

### Driver Breakdown

The recommended driver packing users see in the TurboRPS detail pane, as a formal paginated PDF (`RPSBreakdownPdfService`). One section per RPS circuit with a driver-number-centric header (bold driver Switch IDs + type on the left, circuit info on the right), then a two-column body: the sub-driver channels with their packed fixture segments (wattage + length + split labels) on the left, and the circuit's grouped fixtures on the right. Reuses the exact TurboRPS pipeline (`RpsCircuitDataBuilder` = `CircuitCollectorService` + `DriverSelectionService`) via `RPSCollectorService.CollectBreakdown`, so the packing matches the dashboard. Circuits are kept whole on a page whenever they fit; only a circuit too tall for one page flows across pages. Letter portrait only.

### Data Source

Collects from `OST_LightingDevices` family instances with valid driver parameters (`Power > 0`, `Sub-Driver Power > 0`, power evenly divisible by sub-driver). Circuit info is read from the electrical system connected to each instance. RPS cut sheets are appended after fixture cut sheets in the Cut Sheets tab.

## Cut Sheets Tab

Downloads spec sheet PDFs from lighting fixture and power supply types, stamps a company header/footer on every page, and merges them into a single bookmarked PDF. Password-protected PDFs that cannot be embedded render a placeholder page with the source URL.

### What It Does

1. **Collects fixture and RPS types** — Scans placed `OST_LightingFixtures` and valid `OST_LightingDevices` for unique types with a "Data Sheet URL" parameter. Multiple families sharing a Type Mark collapse to one row; the "primary" variant is chosen by populated URL/CatalogNumber, then by base name (token-subset of all siblings — e.g. `Tape` is preferred over `Tape (Hook)` or `Bar Tape`).
2. **Downloads or loads spec sheets** — Fetches each PDF from the URL via HTTP, or uses a local PDF file if one has been browsed to. Users can set a **default local PDF** per catalog number (gold star) that persists across projects.
3. **Stamps header/footer** — Adds a company header (logo, project name, date, Type Mark) and footer (address, phone, website) to every page.
4. **Merges into one PDF** — Combines all spec sheets into a single output file with PDF bookmarks at each type's first page. Password-protected PDFs render a placeholder page with the source URL.

### Company Settings

Company info (logo, address, phone, website) is saved to `%APPDATA%\TurboSuite\TurboDocsSettings.json` and reused across all projects. Legacy `TurboCutsSettings.json` files are automatically migrated on first load.

## Control BOM Tab

Generates a control system bill of materials PDF from TurboZones panel breakdown data.

### Layout

Three-column table: **Qty | Part Number | Description**, grouped by category with bold category headers and rule lines. Categories include Processors, Panels, Modules, Accessories, and Keypads. The page header includes the brand name (e.g. "LUTRON BILL OF MATERIALS").

### Data Source

Re-derives the panel breakdown using `PanelAllocationService.BuildPanelBreakdown` with saved TurboZones settings (brand, panel size overrides, special device selections) from ExtensibleStorage. Also collects keypad counts and hybrid repeater info from the Revit model. BOM line items are built using the same grouping logic as the TurboZones Panel Breakdown tab.

## Cover Tab

Generates cover page and notes PDFs for lighting fixture and control system specification packages. Toggle between **Fixture Package** (General Notes) and **Control Package** (Control Notes). A cover page with project name, location, subtitle, date, project number, and branding images is prepended before the notes.

### Data Source

Notes are read from Revit key schedules: **Notes_General** and **Notes_Controls**. Each schedule has "Key Name" (note number) and "Comments" (note text) columns. If a schedule is not found, hardcoded defaults are used as a fallback. Notes are displayed read-only in TurboDocs — edits must be made in the Revit schedule.

### Layout

Letter-size PDF. Page 1 is a cover page with project name, location, subtitle, date, project number, and optional branding images (vertical banner top-left, footer banner bottom). Subsequent pages contain numbered notes with word wrapping. Notes pages have a header (project name, subtitle, logo) and footer (company info). The cover page has no header or footer. Project Number is read from Revit's Project Information; Project Location and branding image paths are configured in TurboDocs Settings.

## Dependencies

### Revit Project
- Lighting fixture families with schedule parameters populated (see parameter mapping above)
- Lighting device (RPS) families with Power, Sub-Driver Power, and schedule parameters for the Power Supplies tab
- A **"Data Sheet URL"** shared type parameter on fixture/device types for Cut Sheets (direct URL to a PDF spec sheet)

### Software
- **PdfSharpCore** (NuGet, MIT license) — PDF generation, reading, page stamping, merging, and bookmarks
