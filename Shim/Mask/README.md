# TurboMask

Draws a white masking region over the selection's combined bounds, overlays a view-level annotation "stamp" at each lighting fixture so the fixture's footprint graphics stay readable on top of the mask, redraws any wires connected to masked devices as detail lines on top, and groups region + stamps + wire overlays as `TurboMask N`. Entry `MaskCommand.cs`; selection bounds in `Services/SelectionBoundsService.cs`, stamp extraction/loading in `Services/StampFamilyService.cs`. Floor plan, RCP, or drafting view. Re-selecting a `TurboMask N` group and re-running refreshes it in place.

Existing TurboMask stamps/regions covering the same fixtures are cleaned up before new ones are placed. Tags on the masked fixtures are raised above the stamps so they stay visible. Detail lines in the selection are likewise raised — and kept **outside** the `TurboMask N` group, so refreshing or ungrouping never deletes the user's own linework. The stamp for each fixture family is extracted once from the family's nested **Generic Annotation** and loaded as `Stamp_<FixtureFamilyName>`; later runs reuse it.

## Dependencies

| Category | Role |
|----------|------|
| Lighting Fixtures / Devices, Electrical Fixtures / Equipment | Masked + stamped |
| Any other selected element | Contributes to masked bounds, not stamped |

| Requirement | Purpose |
|-------------|---------|
| Nested **Generic Annotation** sub-family | Source of the footprint stamp; a fixture family without one is skipped and reported |
| At least one **Filled Region** type | Duplicated into the `Masking Region` type (solid white, masking) on first run |
| A solid-fill **drafting** pattern | Background fill for the masking region |
| Line subcategory `Lighting Fixture` / `Wiring` (optional) | Applied to the region boundary / wire-overlay detail lines when present |

> The nested annotation loads under its original name (commonly `Symbol`). If an unrelated family already owns that name, the fixture is skipped to avoid overwriting it — rename/remove the conflict and re-run.

## Notes

- Region, stamps, and group are created in a single undo step. The extracted `Stamp_*` families load **outside** that step, so they survive an undo and are reused next run.
- Masked bounds come from each element's view bounding box. Geometry hidden in some family types (e.g. internal masking regions in a tag family) can still widen the box; adjust the family or resize the region afterward.
- The region orients to the selection's dominant rotation — a diagonal run of rotated devices (e.g. a driver stack) gets a snug rectangle that runs with it. No shared rotation ⇒ axis-aligned box.
- **Dependent views aren't supported.** Their annotations live in the primary view, so TurboMask points to the parent view (named in the prompt) — run it there and the mask shows through every dependent.
- **Wire overlays are visual stand-ins.** Real wires are never modified — they stay fully connected, just hidden under the mask, with a detail-line copy drawn on top. The overlay uses the `Wiring` line style if present, else the default. When a V/G filter restyles the wire (e.g. DMX wires as `Dot` by Wire Type), the overlay picks up that same override to match; if several filters match, the highest-priority one wins. The override is a snapshot at mask time — no tick marks, home-run arrows, or wire tags, and it won't follow the wire or a later filter retune; re-run to refresh.
