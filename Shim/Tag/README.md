# TurboTag

Batch-places type/length tags on the current selection, one family + direction per fixture kind. Entry `TagCommand.cs`; placement math in `Services/TagPlacementService.cs` (see CLAUDE.md "Fixture Transform and Direction Offsets" — BasisX-angle only, no RCP X-flip), linear-run grouping in `Services/LinearRunService.cs`, family/type resolution in `Services/TagTypeService.cs`. Floor plan or RCP. Re-run deletes existing same-family tags per fixture before placing.

## Fixture kind → tag family

| Fixture kind | Tag family | Direction |
|---|---|---|
| Point-based (ceiling/floor) | `AL_Tag_Lighting Fixture (Type)` | Up/Down/Left/Right (prompted) |
| Line-based (linear) | `AL_Tag_Lighting Fixture (Linear Length)` | Up/Down (prompted) |
| Line-based, **Combined** | `AL_Tag_Lighting Fixture (Run Length)` | One tag per continuous run (end-to-end adjacency), on the middle fixture, summed run length |
| Line-based, **Combined Forced** | `AL_Tag_Lighting Fixture (Run Length)` | All selected linears as one run regardless of adjacency — for curved end-caps / geometry that breaks adjacency detection |
| Face-based (wall sconce) | `AL_Tag_Lighting Fixture (Type)` | Auto — offset along wall normal |

Point/face offsets are computed from each fixture's `Symbol Length`, `Symbol Width`, and the type-mark text width.

## Required families / parameters

| Tag family | Note |
|---|---|
| `AL_Tag_Lighting Fixture (Type)` | Point + face-based |
| `AL_Tag_Lighting Fixture (Linear Length)` | Linear — needs types `Tag_Top` / `Tag_Bottom` |
| `AL_Tag_Lighting Fixture (Run Length)` | Combined — needs `Tag_Top` / `Tag_Bottom`, label bound to `Run Length` |
| `AL_Tag_Lighting Device (SwitchID)` | Power-supply devices |
| `AL_Tag_Lighting Device (Keypad)` | Keypads — needs type `2. Two Gang` for two-gang |

| Parameter | On | Purpose |
|---|---|---|
| `Sub-Driver Power` | Lighting Device type | Presence ⇒ power supply (vs. keypad) |
| `Two Gang` | Keypad instance | Selects two-gang tag type |
| `Run Length` | Linear fixture instance | Summed run length on the lead fixture, cleared on others (Combined) |
| `Linear Length` | Linear fixture instance | Per-fixture length, summed for Combined |

Keypads are identified by family name containing "Keypad" (case-insensitive); power supplies by presence of the `Sub-Driver Power` type param.
