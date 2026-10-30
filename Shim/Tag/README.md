# TurboTag

Batch-places type/length tags on the current selection, one family + direction per fixture kind. Entry `TagCommand.cs`; placement math in `Services/TagPlacementService.cs` (see CLAUDE.md "Fixture Transform and Direction Offsets" — BasisX-angle only, no RCP X-flip), linear-run grouping in `Services/LinearRunService.cs`, family/type resolution in `Services/TagTypeService.cs`. Floor plan or RCP. Re-run deletes existing same-family tags per fixture before placing.

## Fixture kind → tag role

Tag families are matched by their `TurboSuite Role` type parameter, not by family name (see `Core/Shared/Constants/Roles.cs`).

| Fixture kind | Tag role | Direction |
|---|---|---|
| Point-based (ceiling/floor) | `FixtureTypeTag` | Up/Down/Left/Right (prompted) |
| Line-based (linear) | `LinearTag` | Up/Down (prompted) |
| Line-based, **Combined** | `RunLengthTag` | One tag per continuous run (end-to-end adjacency), on the middle fixture, summed run length |
| Line-based, **Combined Forced** | `RunLengthTag` | All selected linears as one run regardless of adjacency — for curved end-caps / geometry that breaks adjacency detection |
| Line-based, **vertical** (tape run up a wall) | `LinearTag` / `RunLengthTag` | Same Up/Down/Combined prompt picks the tag type; position is auto — offset along the wall normal past the symbol |
| Face-based (wall sconce) | `FixtureTypeTag` | Auto — offset along wall normal |

Point/face offsets are computed from each fixture's `Symbol Length`, `Symbol Width`, and the type-mark text width.

Any line-based fixture takes the linear path, **even when it is hosted on a wall face** (`IsOnVerticalFace`) or carries a wall role — the face-based path needs a `LocationPoint`, so tape routed there used to get no tag at all. "Vertical" is geometric, not a role (`GeometryHelper.IsVerticalLineBasedFixture`: line within ~3° of plumb), because one tape family is placed both flat and vertical; a tilted run with a real plan projection takes the ordinary linear path.

Any element a placement path skips (no location, Revit refuses the tag, an exception) is listed in one dialog at the end of the run and left selected. Successes are not reported.

## Plan scale

Every tag offset is a 1/4" = 1'-0" reference value scaled by `K = view.Scale / 48` of the active view (`PlanScale`, see CLAUDE.md). `TagCommand` builds one `TagGeometry` (Core) per run and passes it down, so a tag sits the same printed distance from its fixture at 3/16" or 1/8" as at 1/4"; at 1/4" the output is unchanged. Measured symbol and tag extents already come back at view scale, so only the gaps and fallback widths added to them carry `K`. Set the view scale **before** tagging; after a scale change, re-run TurboTag, which replaces its own tags. Power-supply tags sit on the device (zero offset) and are unaffected. TurboDriver's split-fixture linear tags read the same `TagGeometry.LinearOffsetFeet`. `TagGeometryTests` pins every member to its pre-scale value at 1/4" and to linear scaling with `K`.

## Required families / parameters

| Tag role | Note |
|---|---|
| `FixtureTypeTag` | Point + face-based |
| `LinearTag` | Linear — needs types `Tag_Top` / `Tag_Bottom` |
| `RunLengthTag` | Combined — needs `Tag_Top` / `Tag_Bottom`, label bound to `Run Length` |
| `SwitchIdTag` | Power-supply devices |
| `KeypadTag` | Keypads — needs type `2. Two Gang` for two-gang |

| Parameter | On | Purpose |
|---|---|---|
| `Sub-Driver Power` | Lighting Device type | Presence ⇒ power supply (vs. keypad) |
| `Two Gang` | Keypad instance | Selects two-gang tag type |
| `Run Length` | Linear fixture instance | Summed run length on the lead fixture, cleared on others (Combined) |
| `Linear Length` | Linear fixture instance | Per-fixture length, summed for Combined |

Keypads are identified by their `TurboSuite Role` type parameter (`Keypad`; see `Core/Shared/Constants/Roles.cs`); power supplies by presence of the `Sub-Driver Power` type param. Tag families are likewise found by Role (the roles above), not by family name.
