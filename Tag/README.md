# TurboTag

Batch-places lighting fixture type tags on selected fixtures with configurable direction.

**Suggested shortcut:** `TT`

## Fixture Types

| Fixture Type | Tag Family | Direction |
|---|---|---|
| Point-based (ceiling/floor) | `AL_Tag_Lighting Fixture (Type)` | Up, Down, Left, or Right (user choice) |
| Line-based (linear) | `AL_Tag_Lighting Fixture (Linear Length)` | Up or Down (user choice) |
| Face-based (wall sconce) | `AL_Tag_Lighting Fixture (Type)` | Automatic — offset along wall normal |

## Usage

1. Select lighting fixtures in a floor plan or ceiling plan view.
2. Run TurboTag. Direction dialogs appear based on which fixture types are selected.
3. Tags are placed with offsets computed from each fixture's `Symbol Length`, `Symbol Width`, and type mark text width.

Existing tags of the same family are deleted per-fixture before placing new ones. Works in both floor plan and RCP views.
