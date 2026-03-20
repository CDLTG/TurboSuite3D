# TurboCuts

Downloads spec sheet PDFs from lighting fixture types, stamps a company header/footer on every page, and merges them into a single bookmarked PDF.

## What It Does

1. **Collects fixture types** — Scans all placed `OST_LightingFixtures` for unique `FamilySymbol` types that have a "Data Sheet URL" type parameter.
2. **Downloads spec sheets** — Fetches each PDF from the URL via HTTP.
3. **Stamps header/footer** — Adds a company header (logo, project name, date, fixture Type Mark) and footer (address, phone, website) to every page. The original PDF content is scaled to fit within the header/footer boundaries.
4. **Merges into one PDF** — Combines all spec sheets into a single output file with PDF bookmarks at each fixture type's first page.

## Usage

1. Open a project with lighting fixtures that have the "Data Sheet URL" type parameter populated.
2. Run TurboCuts from the Utilities panel.
3. Configure company settings (logo, address, phone, website) — these persist across sessions.
4. Review the fixture checklist. Use Select All / Deselect All to choose which fixtures to include.
5. Click **Generate**, choose an output location, and wait for downloads and merging to complete.

Failed downloads are skipped and reported in the status bar.

## Company Settings

Company info (logo, address, phone, website) is saved to `%APPDATA%\TurboSuite\TurboCutsSettings.json` and reused across all projects.

## Header / Footer Layout

**Header** (top of every page):
- Left: Company logo
- Center: Project name (bold) and date
- Right: "Fixture Type:" label and Type Mark (large bold)
- Separated from content by a horizontal rule

**Footer** (bottom of every page):
- Horizontal rule
- Address and phone number (centered)
- Website (centered)

## Dependencies

### Revit Project
- Lighting fixture families with a **"Data Sheet URL"** shared type parameter containing a direct URL to a PDF spec sheet
- URLs must point directly to PDF files (not HTML pages)

### Software
- **PdfSharpCore** (NuGet, MIT license) — PDF reading, page stamping, merging, and bookmarks
- Internet access to download spec sheets from manufacturer URLs
