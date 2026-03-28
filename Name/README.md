# Name Module

Windowed utility for assigning CAD room names and ceiling heights to filled regions, and interactively generating regions in 2D drafting workflows.

## TurboName (NameCommand)

### Workflow

1. Configure CAD Room Source in TurboSuite Settings (Block mode with block name + attribute tags, or Text mode with layer names and optional block-based ceiling heights).
2. Run TurboName. A dialog opens with two actions: **Run** (assign names) and **Generate** (create regions).
3. **Run**: Collects "Room Region" filled regions and linked DWG data, assigns room names to Comments, and places TextNotes. Shows a summary dialog with processed/skipped/ambiguous/unmatched counts.
4. **Generate**: Opens a sub-dialog with Rectangle (two-click) and Polygon (multi-click) modes for interactively creating filled regions over room areas.

### Behavior

- **Region type filter**: Processes "Room Region", "Room Region (Flagged)", and "Room Region (Empty)" type FilledRegions.
- **Room name**: Written to the region's Comments parameter (forced uppercase, `#` stripped).
- **TextNote placement**: At the CAD block/text source location, not at the region centroid. When a region has 1 name and 1 ceiling height, they are combined into a single TextNote. When a region has 1 name and multiple heights, the name is placed separately and each height is placed at its own CAD location.
- **Ceiling height cleaning**: Strips alphabetical characters, spaces, periods, and leading `+` from raw CAD values (e.g., `+10' - 0" CLG.` becomes `10'-0"`). Preserves ceiling description keywords (Vault, Slope, Barrel, Tray, Tin, Suspend, Drop, Cathedral, Coffer, Dome, Groin) as a separate smaller TextNote below.
- **Project North rotation**: TextNotes are rotated to align with model elements when Rotate Project North has been applied (uses negated `ProjectPosition.Angle`).
- **Text types**: Room name + height use `AL_Annotation_4.5"`, ceiling descriptions use `AL_Annotation_3"`.
- **Re-run safe**: Skips regions that already have both Comments and a matching TextNote. Regions with Comments but no TextNote get TextNotes created using CAD ceiling height data (1:1 combined, 1:many separate) or centroid fallback.
- **Region flagging**: Ambiguous regions (multiple distinct room names) are changed to "Room Region (Flagged)". Unmatched regions (no CAD data) are changed to "Room Region (Empty)". Both are unflagged back to "Room Region" on subsequent successful runs.
- **Deferred extraction**: Expensive operations (region collection, CAD extraction) are deferred behind button clicks to keep the initial dialog fast.
- **Single transaction**: All changes roll back cleanly with Ctrl+Z.

### CAD Source Modes

- **Block mode**: Reads INSERT entities matching a configured block name. Room name is concatenated from ordered attribute tags. Ceiling height from a separate attribute tag.
- **Text mode**: Reads Text/MText entities on configured layers. Room names from the room name layer, ceiling heights from either:
  - A separate ceiling height layer (text entities), or
  - Block attributes (configured via Ceiling Height Block Name + Tag in Settings)
  - Room names and ceiling heights are added as separate entries at their own CAD locations.

### Generate Regions

Interactive region creation with two modes:

- **Rectangle**: Two clicks define opposite corners of a rectangular filled region.
- **Polygon**: Multiple clicks define corners of an arbitrary polygon. Press Escape to close the shape (minimum 3 corners). Guide lines are drawn between selected corners using the "Wiring (Green)" line style for visual feedback, and removed when the region is created.

Both modes loop continuously until the user clicks **Finish** in the dialog.

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
- CAD source mode (Block or Text) must be configured in TurboSuite Settings before running
