# TurboSetup

Running TurboSetup opens a **landing menu** with two actions:

- **Project Setup** — new-project setup automation for the 3D (RVT-linked) lighting workflow (below).
- **Name Spaces from Rooms** — seed Space names from the architect's linked Rooms ([Space naming](#space-naming)).

Entry `SetupCommand.cs`; work split across `Services/` (`LevelCopyService`, `ViewGenerationService`, `ViewRangeService`, `ToposolidVisibilityService`, `SpaceNamingService`), firm standards in `SetupConstants.cs`, per-version link-graphics seam in `Revit{year}/Setup/LinkGraphicsSeam.cs` (see CLAUDE.md "per-shim split file").

## Project Setup

Copy levels from the linked architectural model, generate the firm's Floor Plan and RCP views per level, assign the firm view templates, and wire each view's link graphics to a chosen architectural view.

Starting a lighting project from the firm template otherwise means recreating every level the architect defined, spinning up a Floor + RCP view for each, applying the right template, and hand-configuring RVT Links display on all of them. TurboSetup does that in one pass, driven off the loaded architectural link.

### Two-stage flow

- **Stage 1 — link + levels:** pick the architectural link and the levels to build views for; mark the main level (drives the level-index strings, e.g. `01`, `02`).
- **Stage 2 — view mapping** *(Revit 2025+ only)*: per planned view, choose the linked architectural view it bases display + view range from. Every row defaults to `(none)` — a conscious per-row choice, never auto-guessed.

A summary dialog reports what was copied, created, skipped, and applied.

### What it does

- **Copies levels** from the linked arch model into the host, then deletes the host template's original placeholder levels (switching the active view off them first so the cascade delete is allowed).
- **Creates a Floor Plan and RCP view per selected level**, named `{NN} - Floor - Lighting` / `{NN} - RCP - Lighting`, each baked with its firm template (`AL_Floor Plan` / `AL_RCP`) in one shot so the views stay free to take link overrides.
- **Hides the linked Toposolid** on every `AL_`-prefixed lighting template before view creation, so the suppression carries into generated views and later section/elevation views alike.
- **(Revit 2025) Applies the firm link-graphics hybrid** and copies the architect's view range onto each mapped host view, targeting the link *type* so the override lands on the row the V/G dialog shows.

The whole run is wrapped in a `TransactionGroup` — any failure rolls back cleanly and restores the active view so Revit is never left view-less.

### Requirements

| Requirement | Purpose |
|-------------|---------|
| A **loaded** RVT architectural link | Source of levels and (2025) linked views; an unloaded or absent link is reported and the command exits |
| Firm view templates `AL_Floor Plan` and `AL_RCP` | Applied to generated views; missing templates fail fast before any change |

### Version behavior

- **Revit 2025:** full workflow including Stage 2 view mapping and link-graphics configuration. The firm hybrid needs `LinkVisibility.Custom`, which only the 2025+ API can write.
- **Revit 2024:** levels, views, and templates only. The link step is skipped (Stage 2 doesn't appear) and the summary notes that linked-view display must be set up manually. See `Revit{year}/Setup/LinkGraphicsSeam.cs` for the per-version split.

### Notes

- **3D RVT-linked projects only.** 2D CAD-linked drafting setup is not yet supported — those projects get a clean "not yet supported" message.
- Firm standards (template names, view-name suffixes, the `AL_` prefix) are baked into `SetupConstants` — they live in the host project template, so there's no Settings UI or ExtensibleStorage.
- Views whose target name already exists are skipped (reported in the summary), so re-running is safe.

## Space naming

Runtime room detection reads project-owned **Spaces** (`SpaceRoomFinderService`), not the architect's linked Rooms — so a Space needs a name for its fixtures to resolve. **Name Spaces from Rooms** seeds those names in one pass: for each placed Space, it finds the architect Room at the Space's plan location (via the BAND_ROOM finder, `LinkedRoomFinderService` — the one place architect Rooms are still consulted) and writes that Room's name onto the Space, normalized identically to TurboName (trimmed, `#` removed, UPPERCASED).

Two modes, picked from a second dialog:

- **Name only blank Spaces** *(default)* — leaves any Space you already named untouched, so manual disambiguation splits (e.g. `LOWER POWDER` / `MAIN POWDER` where the architect drew two rooms both named `POWDER`) survive. This is the safe re-run.
- **Force re-pull ALL Spaces** — overwrites every Space name from the architect, including hand-edited ones. Use only when the architect renamed rooms and you want to re-seed from scratch.

A summary reports how many Spaces were examined, named, skipped, and left as-is (no architect Room found), plus a preview of `old → new` names. Spaces with no architect Room beneath them are left unchanged and read blank at runtime — deliberate, so a fixture reads no room rather than a silently wrong one.
