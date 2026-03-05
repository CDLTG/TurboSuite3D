# TurboBubble

Places a switchleg tag and stub wire on a single lighting fixture or electrical fixture.

**Suggested shortcut:** `TB`

## Usage

### Lighting Fixtures
1. Run TurboBubble and click a lighting fixture's **existing tag** (not the fixture itself).
2. Click a second point to set the tag direction (left or right of fixture).
3. A switchleg tag and arc wire are placed automatically.

For fixtures with **Remote Power Supply** enabled, a directional remote switchleg tag (`Switchleg Left` / `Switchleg Right`) is used instead of the standard tag.

### Electrical Fixtures
1. Click an electrical fixture directly (exhaust fan, receptacle, etc.).
2. Click a direction point.
3. Vertical families (exhaust, fireplace igniter) place the tag up/down; others place left/right.

## Required Tag Families

- `AL_Tag_Lighting Fixture (Switchleg)` — standard switchleg
- `AL_Tag_Lighting Fixture (Remote Switchleg)` — RPS switchleg (types: Switchleg Left, Switchleg Right)
- `AL_Tag_Electrical Fixture (Switchleg)` — electrical fixture switchleg

Existing switchleg tags and orphaned stub wires are cleaned up before placement. Works in floor plan and ceiling plan views only.
