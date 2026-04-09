# TurboDocs

Tabbed document generation utility. Five output tabs: **Schedule** (fixture schedule PDF), **Cut Sheets** (spec sheet PDF merging), **Load Schedule** (electrical circuit load schedule PDF), **Panel Schedule** (dimmer panel breakdown PDF), and **Cover** (cover page and general/control notes PDF). A **Settings** tab configures shared company info and page options.

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

A **Specification Notes** block is appended after all fixture entries — up to 6 numbered notes (e.g., contractor instructions, approval requirements). Notes are editable in the UI with sensible defaults and persist across sessions. Empty notes are omitted and the remaining notes renumber sequentially.

### Page Formats

- **8.5" x 28.5"** — construction document strip (default)
- **8.5" x 11"** — standard letter

Page breaks never split a fixture entry. Classification headers are kept with at least one entry. Headers (project name, subtitle, note, logo) repeat on every page. Page numbers appear on letter format only.

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

## Cut Sheets Tab

Downloads spec sheet PDFs from lighting fixture types, stamps a company header/footer on every page, and merges them into a single bookmarked PDF.

### What It Does

1. **Collects fixture types** — Scans all placed `OST_LightingFixtures` for unique `FamilySymbol` types that have a "Data Sheet URL" type parameter.
2. **Downloads spec sheets** — Fetches each PDF from the URL via HTTP.
3. **Stamps header/footer** — Adds a company header (logo, project name, date, fixture Type Mark) and footer (address, phone, website) to every page.
4. **Merges into one PDF** — Combines all spec sheets into a single output file with PDF bookmarks at each fixture type's first page.

### Company Settings

Company info (logo, address, phone, website) is saved to `%APPDATA%\TurboSuite\TurboDocsSettings.json` and reused across all projects. Legacy `TurboCutsSettings.json` files are automatically migrated on first load.

## Cover Tab

Generates cover page and notes PDFs for lighting fixture and control system specification packages. Toggle between **Fixture Package** (General Notes) and **Control Package** (Control Notes). A cover page with project name, location, subtitle, date, project number, and branding images is prepended before the notes.

### Data Source

Notes are read from Revit key schedules: **Notes_General** and **Notes_Controls**. Each schedule has "Key Name" (note number) and "Comments" (note text) columns. If a schedule is not found, hardcoded defaults are used as a fallback. Notes are displayed read-only in TurboDocs — edits must be made in the Revit schedule.

### Layout

Letter-size PDF. Page 1 is a cover page with project name, location, subtitle, date, project number, and optional branding images (vertical banner top-left, footer banner bottom). Subsequent pages contain numbered notes with word wrapping. Notes pages have a header (project name, subtitle, logo) and footer (company info). The cover page has no header or footer. Project Number is read from Revit's Project Information; Project Location and branding image paths are configured in TurboDocs Settings.

## Dependencies

### Revit Project
- Lighting fixture families with schedule parameters populated (see parameter mapping above)
- For Cut Sheets: a **"Data Sheet URL"** shared type parameter containing a direct URL to a PDF spec sheet

### Software
- **PdfSharpCore** (NuGet, MIT license) — PDF generation, reading, page stamping, merging, and bookmarks
