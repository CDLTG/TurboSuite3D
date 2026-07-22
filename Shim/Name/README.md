# Name Module

Single **modeless** window for 2D job setup: fold-in of the linked-CAD layer visibility list, click-to-tag layer roles, room-name/ceiling-height assignment, and interactive region generation — all against the launch view. Every Revit write goes through one `IExternalEventHandler` (`TurboNameApiHandler`; see CLAUDE.md "Modeless pattern").

## TurboName (NameCommand)

### Workflow

The window has three sections in job-setup order (the last two share a group box) — all editing happens inline; settings auto-save to the document when the window closes (no Save button). Section prose is deliberately thin: the window is tall enough already, so anything that isn't a legend or a live readout lives in the tooltip of the control it describes. The window sizes to its content and grows **upward** from its bottom-right anchor — there's no fixed content height. The only bound is set at load from `SystemParameters.WorkArea` (`CapContentToScreen`), purely so a short display gets the outer scroll bar back rather than a title bar off the top of the screen; on a tall display it never binds.

1. **Linked CAD Layers** — the config surface. Row layout mirrors Revit's VG → Imported Categories: `☑ [W][D][A][N][H]` │ **Layer Name** │ **line swatch**. Every layer of every linked CAD (grouped per file, `Find`-filtered) with:
   - a **visibility checkbox** that mirrors VG → Imported Categories and toggles the layer live in the view (persists — `LinkedCadLayerService`);
   - per-row **role toggles**: `W`/`D`/`A` = region-gen wall/door/area, and `N`/`H` = room-name / ceiling-height text (Text mode only). Tagging is **pure data** — it writes the layer's `(file, layer)` into the matching scope with no Revit round-trip; the red watershed preview is shown on demand by the **Preview watershed** toggle (below), not on each tag. The **room-source mode** (Text vs Block) shows at the top of this section as a **read-only status** — it's set automatically by **Pick from view** (click a block → Block, click room-label text → Text) and restored from saved settings, not a manual toggle.
   - a right-pinned **line swatch** rendering the layer's current color / weight / pattern; clicking it opens the **Line Graphics** editor (see below).
   - Tagging writes the layer's `(file, layer)` into the matching scope — **the picked layer's DWG becomes the link scope**, so a co-linked RCP sharing a layer no longer double-seeds each room (fixed). See "Per-link scoping" below.
   - **Preview watershed** — a single toggle (off by default, styled like *Pick from view*) that paints every currently `W`/`D`/`A`-tagged layer red in one pass so you can verify the watershed structure will hold, auto-showing any hidden tagged layer. It's a snapshot of the moment: changing any `W`/`D`/`A` tag while it's on **auto-reverts** it (retag, then press again to re-check). Red is transient — reverted on toggle-off and on window close, never persisted (`LayerRolePreviewService`). While the preview is on (or a pick loop is running), line editing is disabled — the red overlay and the persistent line override share one per-view slot.
   - **Multi-select** — the list is a `ListBox` in `Extended` mode, so **Ctrl/Shift-click layer names** to select several; the visibility checkbox, the `W`/`D`/`A` tags, and a Line Graphics edit then apply to **every selected layer** (native VG's bulk-edit behavior) — the ListBox's own row highlight is the only feedback, no count banner. The in-row controls handle their own mouse-down, so ticking a checkbox edits without collapsing the selection — selection is made by clicking the layer name or empty row space. `N`/`H` deliberately stay **single-row**: they're single-value scopes, so a bulk tag would silently keep only the last. `SelectedItems` isn't bindable (same limitation as `DataGrid` — see CLAUDE.md), so the code-behind pushes the selection to the ViewModel, which decides one-row vs. whole-selection per edit. Bulk visibility and bulk line graphics each cross the external event as **one** list-carrying request — a per-row loop would lose every raise after the first.
   - **Hide by picking** — Revit's *Import Instance ▸ Query ▸ "Hide in view"*, as a loop: press the toggle, then click CAD geometry in the view and its **layer** goes hidden (the row unchecks itself); repeat until **Escape**. Each hide is its own transaction + refresh, so the geometry vanishes under the cursor and the next click can't land on it again. It writes the same view slot as the checkbox, so nothing about it is special to undo. Resolution is geometry → `GraphicsStyle` → `GraphicsStyleCategory`, **guarded** against the one dangerous outcome: an id that isn't a listed layer row is refused, because the import's *parent* category would blank the whole DWG rather than one layer. (Spike-confirmed on `2D SETUP TEST`: polyline, arc, text, hatch face, and block-internal geometry on layer `0` all resolved to a real layer subcategory whose `.Parent` is the import category — the parent case never surfaced, but the guard stays.) A running `PickObject` can't be cancelled from the window, so the button lights up and **disables** rather than offering a toggle-off that couldn't work.
2. **Generate Regions & Names** — one group box, one button row, read in workflow order: **Assign names** on the left, the three region-drawing modes on the right (**Auto-generate** (watershed), **Rectangle**, **Polygon**), inline (no sub-window). Region generation uses the `W`/`D`/`A`-tagged layers and the **Region Type Name** dropdown — the project's existing `FilledRegionType`s (a region type must already exist; region-gen never creates one), defaulting to `Room Region`, refreshed on dropdown-open. The whole row hides while a pick loop is live.
3. **Assign names** (the left button above; formerly its own group box, which cost a header and two paddings for a single button) — collects "Room Region" filled regions + linked DWG data, assigns room names to Comments, places TextNotes, and shows a processed/skipped/ambiguous/unmatched summary.

### Per-link scoping

Room names and ceiling heights are each scoped to their own link independently (`RoomNameLinkName` / `CeilingHeightLinkName`; blank = all links), matched by the Revit-free `Core/Name/CadLinkScope` (unit-tested). Region-gen layer entries are `file|layer`-qualified (a `WALL_*` in the plan is distinct from a same-named RCP layer); a bare entry is legacy, matched by name under the old `SourceLinkName` scope. The extractor skips reading a DWG that supplies neither names nor heights.

### Line Graphics (per-layer VG → Imported Categories *Lines* overrides)

The right-pinned **line swatch** on each layer row both previews and edits the layer's VG *Lines* override, without leaving TurboName for the VG dialog. Clicking it opens a faithful stand-in for Revit's native **Line Graphics** popup (`LineGraphicsDialog`) — **Pattern** (`<No Override>` / `Solid` / the project's `LinePatternElement`s), **Color** (the native Windows `ColorDialog`), **Weight** (`<No Override>` / 1–16), with **Clear Overrides / OK / Cancel**. These are per-view category overrides (`View.SetCategoryOverrides` — `SetProjectionLineColor` / `SetProjectionLineWeight` / `SetProjectionLinePatternId`) and **persist on the view like the visibility checkbox** (never reverted on close, unlike the red preview).

- **Composed off a clone.** The dialog clones the layer's current override and mutates only the three Lines fields, so any surface/halftone overrides survive. Clearing a field uses `-1` / `Color.InvalidColorValue` / `ElementId.InvalidElementId`. Only the final `SetCategoryOverrides` crosses the external event — building/clearing the `OverrideGraphicSettings` is pure value-object work.
- **No coupling special-case.** The red preview and the line override share one per-view slot, but because line edits only ever happen while Preview is off (the swatch is disabled while it's on), the preview's snapshot/restore composes over them for free — "base line settings + transient red overlay" needs no recompose.
- **Custom colors match the template.** The Windows color dialog is seeded with the firm template's three grayscales — RGB `221,221,221` / `187,187,187` / `102,102,102` — in slots 13/14/15 of the 16-slot "Custom colors" panel (2 rows × 8, filled left-to-right), i.e. the same bottom-right corner Revit's own color dialog shows them in, so the muscle memory carries over. Slots are packed **BGR** (`0x00BBGGRR`), not RGB. Anything the user adds via *Add to Custom Colors* is kept for the rest of the Revit session (the array is static; each click builds a fresh dialog) — it isn't persisted to the document.
- **The swatch renders the real style.** Color is exact (or neutral gray when the layer carries none); pattern is drawn from the actual `LinePattern` segments (`LayerLineGraphicsService` caches each pattern's on/off feet array once, normalized per-row into a WPF dash array); weight is a *schematic* thickness (1–16 → ~1–4 px, not exact millimeters). It re-renders live on apply.

### Behavior

- **Region type filter**: Processes "Room Region", "Room Region (Flagged)", and "Room Region (Empty)" type FilledRegions.
- **Room name**: Written to the region's Comments parameter (forced uppercase, `#` stripped).
- **TextNote placement**: At the CAD block/text source location, not at the region centroid. When a region has 1 name and 1 ceiling height, they are combined into a single TextNote. When a region has 1 name and multiple heights, the name is placed separately and each height is placed at its own CAD location.
- **Ceiling height cleaning**: Parses feet, whole inches, and any `n/d` fraction out of the raw CAD value (dropping words, periods, and a leading `+`), **rounds to the nearest inch (half up)** with a foot carry, and reformats as `ft'-in"` — e.g. `+10' - 0" CLG.` → `10'-0"`, `10'-6 1/2"` → `10'-7"`, `10'-11 1/2"` → `11'-0"`. A value with no `'`/`"` mark carries no height and is dropped. Preserves ceiling description keywords (Vault, Slope, Barrel, Tray, Tin, Suspend, Drop, Cathedral, Coffer, Dome, Groin) as a separate smaller TextNote below. The parse/round logic is Revit-free in `Core/Name/CeilingHeightFormatter.cs` (unit-tested).
- **Project North rotation**: TextNotes are rotated to align with model elements when Rotate Project North has been applied (uses negated `ProjectPosition.Angle`).
- **Text types**: Room name + height use `AL_Annotation_4.5"`, ceiling descriptions use `AL_Annotation_3"`.
- **Re-run safe**: Skips regions that already have both Comments and a matching TextNote. Regions with Comments but no TextNote get TextNotes created using CAD ceiling height data (1:1 combined, 1:many separate) or centroid fallback. For regions without Comments (e.g., height-only), an individual TextNote or description placement is skipped only if a matching note (same text, whole-line) already sits at essentially the same spot (within ~0.5 ft) inside the region — so a value repeated at multiple locations in one room (e.g. the same ceiling height called out on the left and right) is placed at each, while a true duplicate at one spot is collapsed.
- **DWG file locking**: If a linked DWG file is open in AutoCAD, TurboName shows a warning dialog identifying the locked file instead of a generic error.
- **Region flagging**: Ambiguous regions (multiple distinct room names) are changed to "Room Region (Flagged)". Unmatched regions (no CAD data) are changed to "Room Region (Empty)". Both are unflagged back to "Room Region" on subsequent successful runs.
- **Deferred extraction**: Expensive operations (region collection, CAD extraction) are deferred behind button clicks to keep the initial dialog fast.
- **Single transaction**: All changes roll back cleanly with Ctrl+Z.
- **CAD redraw after region creation**: New filled regions draw over the linked CAD until Revit regenerates the import, hiding the room-name text underneath (`RefreshActiveView` only repaints — it doesn't trigger the regen). TurboName automates the manual pin/unpin workaround (`NudgeImportGraphics`): it toggles each linked import's `Pinned` state and restores it, forcing the regen so labels stay visible without reopening the view. Best-effort — wrapped so it can never break generation.

### CAD Source Modes

Block name and attribute tags are configured entirely by **clicking** — no AutoCAD and no free typing (removing typed paths that could go stale or corrupt the link scope). **Pick from view** lets you click a room label in the linked CAD and auto-detects the mode (Block vs Text) plus the block/layer by joining Revit's picked layer with the DWG read via ACadSharp; it also **stamps the role and link scope** — the picked layer's `N` tag lights up (Text mode) or the block name + link scope are set (Block mode), scoped to the picked DWG. Because the block is only ever set by a click, the scope is always pinned to the DWG you clicked in — a co-linked plan + RCP sharing a block no longer double-seeds. For a block, the picked room's `value=tag` attribute pairs are shown in the "Detected:" line (e.g. `1-CAR=003, GARAGE=002`) so you can tell which tag holds the room name vs. the ceiling height by reading the values.

The **attribute-tag assignment is a shared pool of dropdowns + pills** (never typed): the Block Name is a Pick-set label, and each tag row is a pick-dropdown on the left with the chosen tag shown as a removable pill on the right. **Room Name Tags** is an ordered multi-pill (the room name is the pill values joined in the order added); **Ceiling Height Tag** is a single pill. The two share the block's attribute pool with mutual exclusion — a tag assigned to one role leaves the other's dropdown. **Height Block Tag** (text-mode height-from-block) is the same dropdown+pill from its own separate block's pool (set by its own **Pick height block** button). Single-select fields (Ceiling Height Tag, Height Block, Height Block Tag) carry a `✕` clear affordance since the dropdowns have no blank entry. (Discovery reads the DWG with ACadSharp, the same license-free path used at extraction time.)

- **Block mode**: Reads INSERT entities matching a configured block name. Room name is concatenated from ordered attribute tags. Ceiling height from a separate attribute tag.
- **Text mode**: Reads Text/MText entities on configured layers. Room names from the room name layer, ceiling heights from either:
  - A separate ceiling height layer (text entities), or
  - Block attributes (configured via Ceiling Height Block Name + Tag in the TurboName window)
  - Room names and ceiling heights are added as separate entries at their own CAD locations.
  - **Multi-line room labels are coalesced.** Where a label is drawn as separate stacked text entities (`BAR/BREAKFAST` over `AREA`, `COVERED` over `TERRACE`), they are merged into one room name at one location (`RoomLabelGrouping`). Otherwise each line would seed its own watershed territory and split the room in half, and the naming pass would see two names in one region and skip it as ambiguous. Two lines are recognized by a vertical gap of one line spacing plus a horizontal offset explicable as centring indent — both keyed to the text height, so it scales with drawing scale. A single multi-line MTEXT already arrives as one entity and is unaffected.

### Generate Regions

Interactive region creation with two modes:

- **Rectangle**: Two clicks define opposite corners of a rectangular filled region.
- **Polygon**: Multiple clicks define corners of an arbitrary polygon. Press Escape to close the shape (minimum 3 corners). Guide lines are drawn between selected corners using the "Wiring (Green)" line style for visual feedback, and removed when the region is created.

Both manual modes loop continuously until the user Escapes out of the pick loop.

- **Auto-generate**: one-shot watershed partition of the whole floor from the CAD room labels (`RegionWatershedService`) — raster distance-transform priority-flood seeded by room labels, bounded by an **Area (`A`) layer** envelope, with block-agnostic door sealing and thin-slot sealing (pocket-door cavities filled so the flood can't finger into them). Wall/door/area geometry comes from the layers tagged `W`/`D`/`A` in the layer table (each `file|layer`-scoped, so a co-linked RCP sharing layer names doesn't add stray geometry), and seeds come from the `N`/room-label source under its own link scope (see "Per-link scoping"), all clipped to the active view's crop box. Each room territory is then vectorized (contour → Douglas-Peucker → edge-to-wall alignment with corners by line intersection, `RegionVectorizer`) and created as a name-less `Room Region` FilledRegion — all in one transaction (single Ctrl+Z). Clean-or-skip: a territory whose aligned boundary self-intersects (a proper crossing or a sub-1" vertex pinch Revit would reject), along with leak/noise territories, is **not** created — it's reported by room name with the reason and location under **"Needs manual"** so the user draws just those by hand. No quietly-wrong regions are ever created. **Needle-finger trim:** where an open area carries duplicate/extra room-name blocks (or a room's flood crosses a doorless opening), the stray label seeds a hairline finger of one owner poking through the gap into a neighbor — which would vectorize into a spurious thin slot. Just after the flood, any room pixel whose own-owner run is flanked by *other room owners* (not walls) within ~20" on both sides is reclaimed by the neighbor, dissolving the finger. A wall-backed narrow room and a normal room-to-room seam are, by construction, never touched. Run **Run** afterward to assign names + TextNotes. Writes a debug image (DEBUG builds only — compiled out of shipped binaries).

## Dependencies

### Required Filled Region Types

| Type Name | Purpose |
|-----------|---------|
| `Room Region` | Standard processed region type |
| `Room Region (Flagged)` | Applied to ambiguous regions; collected and processed on re-run |
| `Room Region (Empty)` | Applied to unmatched regions; collected and processed on re-run |

### Required Text Note Types

| Type Name | Required? | Purpose |
|-----------|-----------|---------|
| `AL_Annotation_4.5"` | Yes -- command aborts if missing | Room name and ceiling height text |
| `AL_Annotation_3"` | Optional | Ceiling description keywords (Vault, Slope, etc.) |

### Required Linked Files

- At least one **linked DWG/DXF** file in the active view containing room name data
- CAD source mode (Block or Text) must be configured in the TurboName window before running
