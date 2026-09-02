# TurboNudge

Slides a point-based family (a keypad, typically) along its wall so it sits exactly **5"** from a picked corner — replacing the manual "place roughly, dimension it, set the witness distance to 5" flow. One command, no Core/VM split — all logic in `NudgeCommand.cs`. First occupant of the **Scripts** pulldown on the Tools panel (`App/TurboSuiteApplication.cs`); meant to be driven by a user-assigned keyboard shortcut, the pulldown being discovery/fallback.

Runs from a Floor Plan or RCP (also Engineering/Area plans). Uses the sole pre-selected point family if there is one — so **place → keystroke → pick corner** is the fast path — otherwise prompts for one. Then `PickPoint` (endpoint + intersection + point snaps) for the corner, and one transaction to move.

## The mechanic — point-keyed, no geometry lookup

The command reads exactly two inputs, and neither is the wall or a line:

1. **The family's transform** → the along-wall direction: `wallDir = (cosθ, sinθ, 0)`, `θ = Atan2(BasisX.Y, BasisX.X)` (via `GeometryHelper.GetTransformAngle` — the suite's blessed [direction rule](../../CLAUDE.md), BasisX only, no BasisY/BasisZ that invert on ceiling-hosted families).
2. **The picked corner point** → a bare `XYZ`. The geometry that produced the snap is discarded the instant you click.

```
d      = (current − corner) · wallDir          signed distance along the wall
move by  wallDir · (sign(d)·offset − d)         slide to `offset`, keeping the side
```

Because the delta is purely along `wallDir` (horizontal, parallel to the wall tangent), **only the along-wall position changes** — elevation and the family's perpendicular standoff from the wall are untouched. `sign(d)` keeps the family on whichever side it was already placed, so wherever you dropped it decides which way the 5" goes. This is why hosted (3D, linked-wall face host) and unhosted (2D, over CAD) families run the **identical** path: nothing reaches into the host.

## Gotchas

- **Accuracy is entirely on the snap.** The command trusts the picked point completely — there's no "is this actually a corner?" check. A clean endpoint/intersection snap gives an exact 5"; a sloppy near-miss gives 5" from the near-miss. The forgiving part: only the point's *projection onto the wall axis* is used, so a corner point offset perpendicular from the wall line still yields the right along-wall distance.
- **The axis follows the family, not the wall.** If a keypad were rotated off its wall's line, the slide would track the keypad's facing, not the wall. Keypads snap flush so the two agree in practice.
- **The 5" is a named constant** (`OffsetFeet = 5.0 / 12.0`, Revit's internal feet) — change it there, or lift it into Settings later.
- Requires a `LocationPoint` family; a non-point selection falls through to the pick, and a picked non-point family is reported and cancelled.
