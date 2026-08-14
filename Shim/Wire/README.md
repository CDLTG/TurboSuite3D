# TurboWire

Creates electrical circuits and wire connections between lighting fixtures and electrical fixtures.

**Suggested shortcut:** `WW`

## Entry Modes

1. **Pre-selected circuits** — Wires all fixtures on each selected circuit as one proximity-sorted (nearest-neighbor) run, regardless of category. No circuit creation. Opens the circuit-info dialog for the wired circuits (setting-gated; switched circuits excluded), even when a comment already exists.
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

New circuits mirror the most recently set-up circuit's panel: the last-used panel in the document, or **left unassigned** when that circuit was deliberately set to `<None>` (DMX/DALI and other circuits that never live on a distribution board). "Switched" circuits are skipped when determining this default — they are unassigned by design and must not poison the remembered panel.

## Circuit Info Dialog

After circuit creation/wiring, the **shared circuit-info dialog** appears for every wired circuit (setting-gated), including circuits that already have a comment — so the room override and panel can be corrected on a re-wire, not just at first creation. The dialog is implemented once in `Shim/Shared` (`CircuitInfoService` + `CircuitInfoDialog`) and used by both TurboWire and TurboDriver; the room-override decision logic is the unit-tested `Core/Circuits/CircuitRoomOverride`. It can be disabled in TurboSuite Settings (`General > Show circuit info dialog`), which turns off the dialog for **both** commands.

The **Comment** field prefills **display-only** with each circuit's *effective* comment — its own Comments param when set, otherwise the fixture-derived comment TurboZones would fall back to (the distinct instance Comments of the circuit's fixtures, joined with `, `, shown raw/un-stripped). It offers autofill suggestions from all existing circuit comments. Because the prefill can be a fixture fallback, an **unchanged** field writes nothing — otherwise it would copy the fixtures' comment onto the circuit as a sticky override that stops tracking them. Only a genuine edit is written, and it applies to the whole batch (blank still means "keep existing"). Prefills blank when a batch's effective comments disagree.

The **Zone** dropdown includes a `<None>` option so circuits that never belong on a panel (DMX/DALI, etc.) can be created unassigned in one step — picking it disconnects any auto-assigned panel via `ElectricalSystem.DisconnectPanel()`. The dropdown defaults to the last circuit's choice (a zone, or `<None>` if the previous one was left unassigned); see [Circuit Creation](#circuit-creation).

It is labelled **Zone** rather than **Panel** because that is what the choice means: the user is declaring which control zone the circuit belongs to, and TurboZones later *recommends* how many panels that zone needs. The mechanism is still Revit's Panel parameter — there is nothing else on a circuit to hold it — so the dropdown lists Electrical Equipment and the code keeps the API's vocabulary. **Shade/control panels are excluded** — a lighting circuit cannot live on them — identified by their **35 V distribution system** (`PanelClassifier`, driven off `RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM`, not the panel name, so a blank-named shade panel is still filtered out). The same rule stops a shade panel from becoming the remembered default in `CircuitService.FindLastPanelChoice`. `PanelAllocationService.ParseLocationNumber` takes only the zone **number** off the selected name (`ZONE 3` → 3; legacy `3-A` → 3, letter discarded), so equipment should be named `ZONE N`.

The **Room Override** field prefills with the circuit's room — resolved from its first fixture the same way TurboZones does (linked Room, or a "Room Region" filled region in 2D drafting jobs), or an existing saved override — and offers project room names for autofill/search. Editing it stores a per-circuit room override in shared ExtensibleStorage (`RoomOverrideStorageService`, read by TurboZones for load naming); the base room is never written, so clearing the field falls back to the geometry. Leaving the field unchanged writes nothing. When a batch of circuits resolves to different rooms, the field shows `<varies>` and stays a no-op unless typed over.

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
