# TurboDocs

Tabbed document generation utility for lighting fixture types. Two tabs: **Schedule** (fixture schedule PDF) and **Cut Sheets** (spec sheet PDF download, stamping, and merging).

## Schedule Tab

Generates a fixture schedule PDF from lighting fixture type parameters.

### Layout

Each fixture entry is a card-style block:
- **Type Mark** in a bordered box on the left (font auto-shrinks for long marks)
- **Catalog Numbers** (pipe-separated, bold) on the first line
- **Manufacturer**, **Description**, **Description2** on lines 2–4
- Three spec sections to the right, each compacting independently (empty values shift remaining values up):
  - **Mechanical**: Finish, Listings, Mounting
  - **Electrical**: Dimming, Watts, Volts
  - **Photometric**: Lumens, CCT, CRI
- **Schedule Notes** below (en-dash bulleted)

Spec sections are dynamically positioned — column gaps adapt to the widest content across all entries while maintaining vertical alignment.

### Page Formats

- **11" x 29"** — construction document strip (default)
- **8.5" x 11"** — standard letter

Page breaks never split a fixture entry. Headers repeat on new pages.

### Parameter Mapping

| Schedule Field | Revit Parameter |
|---------------|-----------------|
| Type Mark | ALL_MODEL_TYPE_MARK (built-in) |
| Catalog #1–6 | Catalog Number1–6 |
| Manufacturer | ALL_MODEL_MANUFACTURER (built-in) |
| Description | ALL_MODEL_DESCRIPTION (built-in) |
| Description 2 | Description2 |
| Finish | Finish1 + Finish2 (concatenated) |
| Listings | Listings and Ratings |
| Mounting | Mounting |
| Dimming | Dimming Protocol |
| Watts | Power (displayed as W, hidden if 0) |
| Volts | Voltage (hidden if both Watts and Lumens are 0) |
| Lumens | Lumens (hidden if 0) |
| CCT | Correlated Color Temperature (CCT) |
| CRI | Color Rendering Index (CRI) |
| Notes 1–6 | Schedule Notes1–6 |

Embedded line feeds (alt+0010) in parameter values are replaced with ", " for single-line display.

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
