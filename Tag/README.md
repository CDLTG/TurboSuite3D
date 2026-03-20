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

## Dependencies

### Required Tag Families

| Family Name | Category | Used For |
|-------------|----------|----------|
| `AL_Tag_Lighting Fixture (Type)` | Lighting Fixture Tags | Point-based and face-based (wall sconce) fixtures |
| `AL_Tag_Lighting Fixture (Linear Length)` | Lighting Fixture Tags | Line-based (linear) fixtures — requires types `Tag_Top` and `Tag_Bottom` |
| `AL_Tag_Lighting Device (SwitchID)` | Lighting Device Tags | Power supply devices |
| `AL_Tag_Lighting Device (Keypad)` | Lighting Device Tags | Keypad devices — requires type `2. Two Gang` for two-gang keypads |

### Required Custom Parameters

| Parameter | On | Type | Purpose |
|-----------|----|------|---------|
| `Sub-Driver Power` | Lighting Device types | Double | Identifies power supplies (vs. keypads) |
| `Two Gang` | Keypad instances | Yes/No (Integer) | Selects two-gang tag type variant |

### Other Requirements

- Active **floor plan or RCP view**
- Keypads identified by family name containing "Keypad" (case-insensitive)
- Power supplies identified by presence of `Sub-Driver Power` type parameter
