# TurboMask

Places a masking region under selected elements and overlays a view-level annotation "stamp" at each lighting fixture, so the fixture's visible footprint graphics stay readable on top of the mask.

## Usage

1. Select the elements to mask (lighting fixtures, devices, electrical fixtures/equipment, and any other elements whose bounds should be covered) in a floor plan, RCP, or drafting view.
2. Run TurboMask.
3. A white **Masking Region** is drawn over the combined bounds of the selection (extended outward by a fixed margin), a stamp is placed at each fixture to redraw its footprint on top of the mask, and the region plus stamps are grouped together as `TurboMask N`.

Existing TurboMask stamps and masking regions covering the same fixtures are cleaned up before new ones are placed, and any tags on the masked fixtures are raised above the stamps so they remain visible. Re-selecting a `TurboMask N` group and re-running refreshes it in place.

The stamp for each fixture family is extracted once from the family's nested **Generic Annotation** and loaded into the project as `Stamp_<FixtureFamilyName>`; subsequent runs reuse it.

## Dependencies

### Supported Categories

| Category | Role |
|----------|------|
| Lighting Fixtures (`OST_LightingFixtures`) | Masked + stamped |
| Lighting Devices (`OST_LightingDevices`) | Masked + stamped |
| Electrical Fixtures (`OST_ElectricalFixtures`) | Masked + stamped |
| Electrical Equipment (`OST_ElectricalEquipment`) | Masked + stamped |

Any other selected element contributes to the masked bounds but is not stamped.

### Fixture Family Requirement

| Requirement | Purpose |
|-------------|---------|
| Nested **Generic Annotation** sub-family | Source of the footprint "stamp" redrawn over the mask. A fixture family with no nested Generic Annotation is skipped and reported. |

> The nested annotation is loaded into the project under its original name (commonly `Symbol`). If the project already contains an unrelated family with that name, the fixture is skipped to avoid overwriting it — rename or remove the conflicting family and re-run.

### Project Requirements

| Requirement | Purpose |
|-------------|---------|
| At least one **Filled Region** type | Duplicated to create the `Masking Region` type (solid white, masking) on first run |
| A solid-fill **drafting** pattern | Background fill for the masking region |
| Line subcategory `Lighting Fixture` (optional) | Applied to the masking-region boundary when present |
| Active **floor plan, RCP, or drafting view** | TurboMask is view-based |

## Notes

- The masking region, stamps, and group are created in a single undo step. The extracted `Stamp_*` families are loaded outside that step, so they remain in the project after an undo (and are reused on the next run).
- The masked bounds come from each element's view bounding box. Geometry that is hidden in certain family types (e.g. internal masking regions in a tag family) can still widen the box; adjust the family or resize the region afterward if needed.
