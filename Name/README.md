# Name Module

Headless command for assigning CAD room names and ceiling heights to filled regions in 2D drafting workflows.

## TurboName (NameCommand)

### Workflow

1. Draw filled regions using the "Room Region" type over room areas in a floor plan view.
2. Configure CAD Room Source in TurboSuite Settings (Block mode with block name + attribute tags, or Text mode with layer names).
3. Run TurboName.
4. Confirm execution in the pre-run dialog (shows region counts, CAD source mode, and view name).
5. Command reads linked DWG files via ACadSharp, extracts room names and ceiling heights, and assigns them to regions.

### Behavior

- **Region type filter**: Only processes "Room Region" type FilledRegions.
- **Room name**: Written to the region's Comments parameter (forced uppercase, `#` stripped).
- **TextNote placement**: At the CAD block/text source location, not at the region centroid. One TextNote per CAD entry inside the region.
- **Ceiling height cleaning**: Strips alphabetical characters, spaces, and periods from raw CAD values (e.g., `10' - 0" CLG.` becomes `10'-0"`). Preserves ceiling description keywords (Vault, Slope, Barrel, Tray, Tin, Suspend, Drop, Cathedral, Coffer, Dome, Groin) as a separate smaller TextNote below.
- **Text types**: Room name + height use `AL_Annotation_4.5"`, ceiling descriptions use `AL_Annotation_3"`.
- **Re-run safe**: Skips regions that already have both Comments and a matching TextNote. Regions with Comments but no TextNote get a TextNote created (using CAD location or centroid fallback).
- **Ambiguity detection**: Regions containing multiple distinct room names are flagged and listed in the summary dialog.
- **Single transaction**: All changes roll back cleanly with Ctrl+Z.

### CAD Source Modes

- **Block mode**: Reads INSERT entities matching a configured block name. Room name is concatenated from ordered attribute tags. Ceiling height from a separate attribute tag.
- **Text mode**: Reads Text/MText entities on configured layers. Pairs room names with nearest ceiling height text by proximity.
