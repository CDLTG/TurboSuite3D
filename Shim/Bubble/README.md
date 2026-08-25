# TurboBubble

Places a switchleg tag and stub wire on a single lighting fixture or electrical fixture.

**Suggested shortcut:** `TB`

## Usage

### Lighting Fixtures
1. Run TurboBubble and click a lighting fixture's **existing tag** (not the fixture itself).
2. Click a second point to set the tag direction (left or right of fixture).
3. A switchleg tag and arc wire are placed automatically.

For fixtures with **Remote Power Supply** enabled, a directional remote switchleg tag (`Switchleg Left` / `Switchleg Right`) is used instead of the standard tag.

For **linear** point fixtures (light bars — a LocationPoint family whose long plan extent is ≥ 3× its short one, the same test TurboWire uses via `GeometryHelper.TryGetLinearLongAxis`), the wire's connector-end vertex is anchored at the bar's **end** rather than its center, so the switchleg arc springs from the end the tag sits off — no manual v1 drag. The end is the long-axis end toward the wire (nearest `Vertex2`, i.e. the side the user clicked); the tag and the `Vertex2`/`Vertex3` arc are unchanged, and the electrical connection is untouched (a display-only vertex move). Non-linear fixtures (square downlights) keep the center anchor. This applies to the horizontal (ceiling/floor) path only — line-based, wall-sconce, chandelier, and electrical-fixture paths are unaffected.

### Electrical Fixtures
1. Click an electrical fixture directly (exhaust fan, receptacle, etc.).
2. Click a direction point.
3. Vertical families (exhaust, fireplace igniter) place the tag up/down; others place left/right.

## Dependencies

### Required Tag Families

| Family Name | Category | Types Required |
|-------------|----------|----------------|
| `AL_Tag_Lighting Fixture (Switchleg)` | Lighting Fixture Tags | (default type) |
| `AL_Tag_Lighting Fixture (Remote Switchleg)` | Lighting Fixture Tags | `Switchleg Left`, `Switchleg Right` |
| `AL_Tag_Electrical Fixture (Switchleg)` | Electrical Fixture Tags | (default type) |

### Required Custom Parameters

| Parameter | On | Type | Purpose |
|-----------|----|------|---------|
| `Remote Power Supply` | Lighting Fixture types | Yes/No (Integer) | Selects remote vs. standard switchleg tag |
| `Scale Factor` | Fixture instances | Double | Scales wire offset distances for wall sconces |

### Recognized Electrical Fixture Families

These family names trigger special placement behavior (vertical or ceiling-fan offsets):

- `AL_Electrical Fixture_Exhaust (Hosted)` / `Exhaust`
- `AL_Electrical Fixture_Fireplace Igniter` / `Fireplace Igniter`
- `AL_Electrical Fixture_Ceiling Fan (Hosted)` / `Ceiling Fan`
- `AL_Decorative_Wall Sconce (Hosted)` (special wire offset)

### Recognized Chandelier (Decorative Pendant) Families

These lighting fixture families use diagonal-corner switchleg placement (one of four corners picked by combining the type tag's side with the user click), with the wire arc curving away from the type tag:

- `AL_Decorative_Pendant (Hosted)`
- `AL_Decorative_Pendant`
- `Z_Chandelier`

### Other Requirements

- At least one **WireType** in the project
- Fixtures must have **electrical connectors**
- Active **floor plan or RCP view**

Existing switchleg tags and orphaned stub wires are cleaned up before placement.
