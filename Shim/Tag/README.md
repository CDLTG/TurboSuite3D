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
| Face-based (wall sconce) | `FixtureTypeTag` | Auto — offset along wall normal |

Point/face offsets are computed from each fixture's `Symbol Length`, `Symbol Width`, and the type-mark text width.

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
