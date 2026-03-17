# TurboWire

Creates electrical circuits and wire connections between lighting fixtures and electrical fixtures.

**Suggested shortcut:** `WW`

## Entry Modes

1. **Pre-selected circuits** — Wires all fixtures on each selected circuit, sorted by proximity (nearest-neighbor). No circuit creation. Prompts for circuit comments if any selected circuit has no existing comment.
2. **Pre-selected fixtures (2+)** — Creates or joins circuits as needed, wires the selected fixtures, then prompts for circuit comments. Lighting and Electrical fixtures are processed as separate groups (cannot share a circuit). Rejects selections spanning multiple existing circuits.
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

## Wire Routing

| Fixture Type | Routing | Details |
|---|---|---|
| Standard (ceiling/floor) | Arc | 24° arc angle |
| Wall sconces | Spline | Wall-normal offsets, 2.5" connector offset, scaled to fixture distance |
| Receptacles | Spline | Wall-normal offsets, 3.0" connector offset |

For multi-fixture runs, arc direction is chosen to avoid overlapping existing tags. Existing wires between two fixtures are deleted before placing new ones.
