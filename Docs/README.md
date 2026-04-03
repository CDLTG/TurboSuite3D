# TurboDocs

Tabbed document generation utility. Three output tabs: **Schedule** (fixture schedule PDF), **Cut Sheets** (spec sheet PDF merging), and **Load Schedule** (electrical circuit load schedule PDF). A **Settings** tab configures shared company info and page options.

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

Generates a load schedule PDF from electrical circuit parameters, listing each circuit with its connected lighting fixtures.

### Layout

Each circuit entry is a single row with fixture details below:
- **Circuit Number**, **Load Name**, **Load Classification**, and **Total VA** on one line
- Connected fixtures listed below with en-dash prefix, grouped by Type Mark
- Point-based fixtures show quantity: `– A3 (x8)`
- Line-based fixtures show total length: `– TL-32'-4"`

### Parameter Mapping

| Field | Source |
|-------|--------|
| Circuit Number | RBS_ELEC_CIRCUIT_NUMBER |
| Load Name | RBS_ELEC_CIRCUIT_NAME |
| Load Classification | Load Classification Abbreviation |
| Total VA | RBS_ELEC_APPARENT_LOAD |
| Fixture Type Mark | ALL_MODEL_TYPE_MARK |
| Linear Length | Linear Length (instance, for line-based fixtures) |

## Cut Sheets Tab

Downloads spec sheet PDFs from lighting fixture types, stamps a company header/footer on every page, and merges them into a single bookmarked PDF.

### What It Does

1. **Collects fixture types** — Scans all placed `OST_LightingFixtures` for unique `FamilySymbol` types that have a "Data Sheet URL" type parameter.
2. **Downloads spec sheets** — Fetches each PDF from the URL via HTTP.
3. **Stamps header/footer** — Adds a company header (logo, project name, date, fixture Type Mark) and footer (address, phone, website) to every page.
4. **Merges into one PDF** — Combines all spec sheets into a single output file with PDF bookmarks at each fixture type's first page.

### Company Settings

Company info (logo, address, phone, website) is saved to `%APPDATA%\TurboSuite\TurboDocsSettings.json` and reused across all projects. Legacy `TurboCutsSettings.json` files are automatically migrated on first load.

## Dependencies

### Revit Project
- Lighting fixture families with schedule parameters populated (see parameter mapping above)
- For Cut Sheets: a **"Data Sheet URL"** shared type parameter containing a direct URL to a PDF spec sheet

### Software
- **PdfSharpCore** (NuGet, MIT license) — PDF generation, reading, page stamping, merging, and bookmarks
