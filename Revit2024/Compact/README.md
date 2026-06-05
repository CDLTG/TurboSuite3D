# TurboCompact

Cleans and compacts the active Revit family document (.rfa). Must be run from the Family Editor.

**Suggested shortcut:** `Ctrl+Shift+S`

## What It Does

1. **Removes unused materials** — Collects all materials, identifies which are referenced by elements and subcategory appearances, and deletes the rest. System materials that can't be deleted are silently skipped.
2. **Compact saves** — Saves with `SaveOptions { Compact = true }` to defragment and reduce file size.

## Usage

1. Open a family document in the Revit Family Editor.
2. Run TurboCompact. A confirmation dialog lists the operations and family name.
3. Click **Proceed** to execute.

Shows an error if run from a project document (not a family).

## Dependencies

- Must be run from the **Family Editor** (`.rfa` document open and active)
- No custom parameters, families, or project-level setup required
