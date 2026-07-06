# TurboWire

Creates electrical circuits and wire connections between lighting fixtures and electrical fixtures.

**Suggested shortcut:** `WW`

## Entry Modes

1. **Pre-selected circuits** — Wires all fixtures on each selected circuit as one proximity-sorted (nearest-neighbor) run, regardless of category. No circuit creation. Prompts for circuit comments if any selected circuit has no existing comment.
2. **Pre-selected fixtures (2+)** — Creates or joins a **single** circuit for the whole selection (Lighting and Electrical fixtures can share one circuit — e.g. a relay-switched closet), wires one nearest-neighbor run through all of them regardless of category, then prompts for circuit comments. Rejects selections that already span multiple existing circuits, or whose fixtures have mismatched voltages (Revit can't form the circuit — surfaced as a plain-language "voltage mismatch" message instead of Revit's cryptic error).
3. **Pre-selected fixture (1)** — Creates a circuit if none exists and prompts for circuit comments. If the circuit already has a comment, silently deselects and does nothing.
4. **Manual pick** — If nothing is selected, prompts for first and second fixture. Wire only — no circuit creation or comments dialog.

## Circuit Creation

| Selection State | Action |
|---|---|
| All uncircuited | Create new circuit from all fixtures |
| Some circuited, some not | Add uncircuited fixtures to the existing circuit |
| All on same circuit | No circuit changes |
| Multiple circuits | Error — no changes made |

New circuits are automatically assigned to the last-used panel in the document (determined by the most recently created circuit with a panel).

## Comments Dialog

After circuit creation/wiring, a comments dialog appears if the circuit has no existing comment. The dialog displays the circuit number and offers autofill suggestions from all existing circuit comments in the document. The dialog can be disabled in TurboSuite Settings (`General > Show circuit comments dialog`).

The dialog also has a **Room Override** field. It prefills with the circuit's room — resolved from its first fixture the same way TurboZones does (linked Room, or a "Room Region" filled region in 2D drafting jobs), or an existing saved override — and offers project room names for autofill/search. Editing it stores a per-circuit room override in shared ExtensibleStorage (`RoomOverrideStorageService`, read by TurboZones for load naming); the base room is never written, so clearing the field falls back to the geometry. Leaving the field unchanged writes nothing. When a batch of circuits resolves to different rooms, the field shows `<varies>` and stays a no-op unless typed over.

## Wire Routing

| Condition | Routing | Details |
|---|---|---|
| Wall sconces (same orientation) | Spline | Wall-normal offsets, 2.5" connector offset, scaled to fixture distance |
| Receptacles (same orientation) | Spline | Wall-normal offsets, 3.0" connector offset |
| Remote Power Supply pairs | Straight | Chamfer wire, no arc |
| On-axis fixtures | 24° arc | Fixtures aligned horizontally or vertically |
| Off-axis, roughly diagonal | Corner arc | 4-point smoothed corner (dx/dy ratio ≥ 0.6) |
| Off-axis, elongated | S-spline | 4-point S-curve stepping along the longer axis |

When both fixtures share a non-axis-aligned rotation (e.g., fixtures on a rotated grid), the on-axis vs off-axis decision is evaluated in the fixtures' local coordinate frame. This ensures inline rotated fixtures receive the 24° arc rather than an incorrect S-curve.

For multi-fixture runs, arc direction is determined by: (1) existing tag positions, then (2) outward from the group centroid, then (3) default. Existing wires between two fixtures are deleted before placing new ones.

### Switch Handling

Switches are wired with an endpoint offset to prevent visual overlap. Wall-hosted switches offset 9" along the wall normal; unhosted switches offset 0.01" along their local Y axis. Switch selections create a single circuit across all fixture categories with the comment "switched" (no comments dialog).

## Dependencies

### Required Custom Parameters

| Parameter | On | Type | Purpose |
|-----------|----|------|---------|
| `Scale Factor` | Fixture instances | Double | Scales spline offsets for wall sconces and receptacles |
| `Remote Power Supply` | Lighting Fixture types | Yes/No (Integer) | Read during circuit analysis |

### Recognized Fixture Families

These family names trigger special wire routing (spline instead of arc):

- `AL_Decorative_Wall Sconce (Hosted)` — wall-normal spline offsets
- `AL_Electrical Fixture_Receptacle (Hosted)` / `Receptacle` — wall-normal spline offsets

### Other Requirements

- At least one **WireType** in the project
- Fixtures must have **electrical connectors** (MEP domain)
- At least one **Electrical Equipment** (panel) in the project for auto-assignment of new circuits
- Active view must support wire placement
