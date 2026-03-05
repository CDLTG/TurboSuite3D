# TurboWire

Creates wire connections between lighting fixtures and electrical fixtures.

**Suggested shortcut:** `WW`

## Entry Modes

1. **Pre-selected circuits** — Wires all fixtures on each selected circuit, sorted by proximity (nearest-neighbor).
2. **Pre-selected fixtures** — Wires the selected fixtures. Lighting and Electrical fixtures are processed as separate groups.
3. **Manual pick** — If nothing is selected, prompts for first and second fixture.

## Wire Routing

| Fixture Type | Routing | Details |
|---|---|---|
| Standard (ceiling/floor) | Arc | 24° arc angle |
| Wall sconces | Spline | Wall-normal offsets, 2.5" connector offset, scaled to fixture distance |
| Receptacles | Spline | Wall-normal offsets, 3.0" connector offset |

For multi-fixture runs, arc direction is chosen to avoid overlapping existing tags. Existing wires between two fixtures are deleted before placing new ones.
