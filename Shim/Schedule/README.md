# TurboSchedule

A page-per-Type-Mark, form-view spec editor for lighting fixtures (`OST_LightingFixtures`) and drivers (`OST_LightingDevices`) — one screen that unifies the two native Revit spec schedules.

Instead of scrolling a wide schedule grid, TurboSchedule shows **one Type Mark per page** with its fields laid out in labeled sections (Identity Data, Electrical, Mechanical, Photometric, Schedule Notes). Every value you edit is written back to **all symbols sharing that Type Mark**, and the whole save lands in a single undo step.

## Usage

1. Run TurboSchedule (must be in a project with no open transaction).
2. Pick a Type Mark from the **Type** dropdown, or step through with the ◀ ▶ arrows.
3. Edit fields in place. A blue dot marks a field (and its page) with unsaved edits.
4. Click **Save** to flush every dirty page in one transaction, or **Discard** to drop all unsaved edits.

The window is modeless — leave it open while you work in Revit. **Close** (footer), the ✕, or **Esc** all close it (you'll be prompted if there are unsaved changes). Reopening within the same Revit session returns you to the type you were last on; a since-removed type falls back to the first page.

## Field states

Each field is reconciled across every symbol under the Type Mark and shows one of four states:

| State | Looks like | Meaning |
|-------|-----------|---------|
| **normal** | editable value | All symbols agree; edit freely. |
| **⟨varies⟩** | grey `⟨varies⟩` placeholder (or an indeterminate checkbox dash) | Symbols disagree. Left untouched it stays out of Save, so legitimate per-symbol differences are never flattened. Typing/clicking resolves it and applies your value to all. |
| **read-only** 🔒 | greyed, locked | Formula-driven or API-read-only on a symbol; shown but not writable. |
| **n/a** | grey `n/a` | The parameter is absent on a symbol in the group (a family-authoring mismatch). Never written. |

Yes/No parameters render as checkboxes; unit-bearing numbers display and parse through Revit's Project Units (so units and rounding match the rest of the model).

## Copy / Paste

An in-app clipboard (not the OS clipboard) copies a whole type or a single section:

- **Copy Type** then **Paste** — same kind only (fixture→fixture, driver→driver).
- **Copy / Paste \<Section\>** (right-click a section header, or the **Copy ▾** menu) — works across kinds where the fields overlap.

⟨varies⟩ and locked fields are excluded at copy time, so a paste never overwrites a target with a non-value.

## Excel round-trip (Sync workbook)

For designers who don't run Revit, TurboSchedule keeps a **per-project `.xlsx` workbook** that they edit and a Revit user reconciles. It's driven by the single **Sync workbook** footer button — the workbook is the **source of truth for spec fields**, so one press does both directions:

1. **Pull** the designers' edited cells into the model (workbook → model), in one transaction.
2. **Refresh** the workbook from the now-current model (model → workbook): append rows for newly-placed Type Marks, flag/purge removed ones.

The **first** press (no workbook yet) prompts a Save-As beside the `.rvt`, creates the workbook, and seeds both sheets — nothing to pull. The chosen path is remembered per project (its own ExtensibleStorage schema), so later presses reuse it silently. The button is disabled while the form has unsaved in-form edits (Save or Discard first) — otherwise the pull/refresh would fight your unsaved changes.

**Consequence to communicate:** once the workbook exists, spec fields edited *directly* in the form are overwritten by the workbook on the next Sync — last-writer-wins, no preview.

### Workbook layout

- Two sheets, **one row per Type Mark**: `Fixtures` and `Drivers` (sheet = Kind). A hidden `_meta` sheet records project path / Revit version / timestamp (drives the wrong-project warning).
- Three-row header: colored **section band** / **field label** / **units**. Type Mark column and the header rows are frozen; the sheet is protected with only the editable cells unlocked.
- Catalog #, Catalog Qty, and Notes are **collapsible column groups** (`[+]/[−]`). `Remote Power Supply` shows as **`RPS`** (a display alias normalized back on read). Dropdowns on Yes/No and Dimming Protocol.
- Numbers are stored **bare** where the unit is a clean scalar (unit lives in the header row); length/compound units (Ceiling Thickness, Power/Length, Efficacy) stay verbatim.

### Cell states (color-only)

| Fill | Meaning |
|------|---------|
| dark grey | parameter **missing** (n/a) — locked |
| light grey | parameter **locked** (read-only), and the Type Mark key column |
| amber | **varies** across instances — edit to unify, blank to keep |
| red | Type Mark **no longer placed** in the model |

A **blank cell = skip** (no change). To deliberately empty a String field, type the `<clear>` sentinel.

### Removed types — one-cycle grace

A Type Mark deleted in Revit turns **red** on the next Sync (a warning). If it's *still* gone on the following Sync its row is **deleted**; if it returned, the red clears. This bounds accumulation — at the cost of losing that row's authored spec if a deletion isn't caught within the cycle.

### The Sync report

A dialog appears only when there's something worth seeing — a **blocking** duplicate Type Mark (aborts the whole sync), junk in a Yes/No cell, a bad Catalog #/Qty token (written anyway, warned), a value the writer couldn't apply, or a locked cell someone overrode. A clean sync just updates the status line (e.g. `Updated 4 types, 9 fields; workbook +2 new`). Unchanged/empty locked cells, unmatched rows, and unknown columns are handled silently.

## Dependencies

| Requirement | Purpose |
|-------------|---------|
| Placed lighting fixture and/or driver instances with a non-blank **Type Mark** | The Type Mark is the page key; types with no Type Mark are skipped |
| The spec parameters on those families (see the `FieldDef` roster) | Each roster field maps to a built-in or shared/custom type parameter; an absent one shows as **n/a** |

## Notes

- **Save is by Type Mark, re-resolved at save time.** The writer re-collects symbols by Type Mark + category when you Save, so it writes the current model membership. A value Revit can't parse (`SetValueString` fails) is skipped, reported in the status line, and left dirty — it doesn't abort the rest of the batch.
- **Discard can't be undone by Revit.** Discarded edits were never committed to the model, so Revit's Undo can't bring them back. Discard restores each field to its loaded value — including the ⟨varies⟩ placeholder/dash for fields that started mixed.
- **Catalog # / Qty** fields accept the same Counts grammar used by TurboDocs — the **Counts syntax ↗** link on the page opens the cheat sheet.
- **Schedule Notes** rows show a live character count to the right of each field, turning red at 80 characters to flag entries that may overrun the schedule column.
- The Fixture and Driver pages share most sections but diverge: Photometric is fixtures-only; Sub-Driver Power / Maximum Fixtures / Derating Factor / Amps Per Channel are drivers-only; DMX Bundle Size is fixtures-only. The DMX group (DMX Channels, DMX Bundle Size, Amps Per Channel) sits at the bottom of Electrical and shows as **n/a** on non-DMX families.
