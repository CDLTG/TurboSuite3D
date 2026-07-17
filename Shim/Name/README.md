# Name Module

Windowed utility for assigning CAD room names and ceiling heights to filled regions, and interactively generating regions in 2D drafting workflows.

## TurboName (NameCommand)

### Workflow

1. Run TurboName. The window hosts the **CAD Room Source** + **Region Generation Layers** config inline (Block mode with block name + attribute tags, or Text mode with layer names and optional block-based ceiling heights), plus a **Source Link** dropdown and the **Run** / **Generate** actions. Edits auto-save to the document when the window closes (no explicit Save button).
2. **Run**: Collects "Room Region" filled regions and linked DWG data, assigns room names to Comments, and places TextNotes. Shows a summary dialog with processed/skipped/ambiguous/unmatched counts.
3. **Generate**: Opens a sub-dialog with Rectangle (two-click) and Polygon (multi-click) modes for interactively creating filled regions over room areas, plus **Auto-generate** (see below).

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

### CAD Source Modes

Block name, attribute tags, and layer names can be discovered in-app — no AutoCAD needed. In the TurboName window → CAD Room Source, **Pick from view** lets you click a room label in the linked CAD and auto-detects the mode (Block vs Text) plus the block/layer by joining Revit's picked layer with the DWG read via ACadSharp. For a block, the picked room's `value=tag` attribute pairs are shown in the "Detected:" line (e.g. `1-CAR=003, GARAGE=002`) so you can tell which tag holds the room name vs. the ceiling height by reading the values. The Block Name / layer / tag fields are also editable, type-ahead dropdowns populated from the linked DWGs as a fallback, and still accept free-typed values. (Discovery reads the DWG with ACadSharp, the same license-free path used at extraction time.)

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

Both manual modes loop continuously until the user clicks **Finish** in the dialog.

- **Auto-generate** (in development): one-shot watershed partition of the whole floor from the CAD room labels (`RegionWatershedService`) — raster distance-transform priority-flood seeded by room labels, bounded by an **Area layer** envelope, with block-agnostic door sealing and thin-slot sealing (pocket-door cavities filled so the flood can't finger into them). Reads wall/door/area geometry from the DWG named in the **Source Link** dropdown (pick the floor plan so a co-linked RCP sharing layer names doesn't add stray geometry), scoped to the active view's crop box. **Note:** Source Link currently scopes that geometry only — room-name and ceiling-height text is still read from *every* linked CAD in the view, so a co-linked RCP carrying the room-name layer seeds every room twice and splits it. See roadmap TurboName-9. Each room territory is then vectorized (contour → Douglas-Peucker → edge-to-wall alignment with corners by line intersection, `RegionVectorizer`) and created as a name-less `Room Region` FilledRegion — all in one transaction (single Ctrl+Z). Clean-or-skip: a territory whose aligned boundary self-intersects (a proper crossing or a sub-1" vertex pinch Revit would reject), along with leak/noise territories, is **not** created — it's reported by room name with the reason and location under **"Needs manual"** so the user draws just those by hand. No quietly-wrong regions are ever created. **Needle-finger trim:** where an open area carries duplicate/extra room-name blocks (or a room's flood crosses a doorless opening), the stray label seeds a hairline finger of one owner poking through the gap into a neighbor — which would vectorize into a spurious thin slot. Just after the flood, any room pixel whose own-owner run is flanked by *other room owners* (not walls) within ~20" on both sides is reclaimed by the neighbor, dissolving the finger. A wall-backed narrow room and a normal room-to-room seam are, by construction, never touched. Run **Run** afterward to assign names + TextNotes. Still writes a debug image (dev aid, removed before ship). Configured via the **Region Generation Layers** fields (Wall / Door / Area layers, Region Type Name).

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
