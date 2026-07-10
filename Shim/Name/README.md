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
- **Ceiling height cleaning**: Strips alphabetical characters, spaces, periods, and leading `+` from raw CAD values (e.g., `+10' - 0" CLG.` becomes `10'-0"`). Preserves ceiling description keywords (Vault, Slope, Barrel, Tray, Tin, Suspend, Drop, Cathedral, Coffer, Dome, Groin) as a separate smaller TextNote below.
- **Project North rotation**: TextNotes are rotated to align with model elements when Rotate Project North has been applied (uses negated `ProjectPosition.Angle`).
- **Text types**: Room name + height use `AL_Annotation_4.5"`, ceiling descriptions use `AL_Annotation_3"`.
- **Re-run safe**: Skips regions that already have both Comments and a matching TextNote. Regions with Comments but no TextNote get TextNotes created using CAD ceiling height data (1:1 combined, 1:many separate) or centroid fallback. For regions without Comments (e.g., height-only), individual TextNote and description placements are skipped if a matching note already exists inside the region boundary.
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

### Generate Regions

Interactive region creation with two modes:

- **Rectangle**: Two clicks define opposite corners of a rectangular filled region.
- **Polygon**: Multiple clicks define corners of an arbitrary polygon. Press Escape to close the shape (minimum 3 corners). Guide lines are drawn between selected corners using the "Wiring (Green)" line style for visual feedback, and removed when the region is created.

Both manual modes loop continuously until the user clicks **Finish** in the dialog.

- **Auto-generate** (in development): one-shot watershed partition of the whole floor from the CAD room labels (`RegionWatershedService`) — raster distance-transform priority-flood seeded by room labels, bounded by an **Area layer** envelope, with block-agnostic door sealing. Reads the DWG named in the **Source Link** dropdown (pick the floor plan so a co-linked RCP sharing layer names doesn't add stray geometry), scoped to the active view's crop box. Currently reports the partition + writes a debug image; FilledRegion creation is the remaining work. Configured via the **Region Generation Layers** fields (Wall / Door / Area layers, Region Type Name).

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
