# Driver Module

Contains two commands that share the same services, models, and driver selection algorithm.

## TurboDriver (DriverCommand)

Headless command for deploying power supplies on a per-circuit basis.

### Workflow

1. Pre-select lighting fixtures with `Remote Power Supply` type parameter enabled.
2. Run TurboDriver (suggested shortcut: TD).
3. Command creates an electrical circuit if one doesn't exist, or uses the existing one.
4. Evaluates the circuit and determines the recommended power supply type and quantity.
5. Deletes any existing power supplies on the circuit (preserving Switch ID).
6. Prompts: select an existing power supply to stack below, or press Esc to pick a bare point.
7. Places power supplies in a column (9" apart), connects to circuit, sets suffixed Switch IDs (e.g., X01a, X01b), and tags each with SwitchID and Switchleg tags.

## TurboRPS (RPSCommand)

Review window for inspecting power supply assignments across all RPS circuits.

### How It Works

1. Scans all circuits with at least one Lighting Fixture that has the `Remote Power Supply` type parameter enabled.
2. For each circuit, reads fixture wattage, manufacturer, dimming protocol, and voltage.
3. Evaluates all loaded driver family types (must have both `Power` and `Sub-Driver Power` parameters; `Power` must be an integer multiple of `Sub-Driver Power`).
4. Runs **First-Fit Decreasing bin-packing** to assign fixtures to sub-drivers, recursively splitting high-wattage fixtures across multiple slots.
5. Recommends the best driver type per circuit, prioritizing: manufacturer match, fewest physical drivers, fewest sub-drivers.

### Usage

1. Run TurboRPS (no pre-selection needed — scans the entire project).
2. The window shows each qualifying circuit with its fixtures, wattage, and recommendation.
3. Use the **dropdown** on each device group to change the family type — changes are written to Revit immediately.

Requires at least one loaded Lighting Device family type with valid `Power` and `Sub-Driver Power` parameters.

## Dependencies

### Required Tag Families

| Family Name | Category | Purpose |
|-------------|----------|---------|
| `AL_Tag_Lighting Device (SwitchID)` | Lighting Device Tags | Tags Switch ID on placed power supplies |
| `AL_Tag_Lighting Device (Switchleg)` | Lighting Device Tags | Switchleg tag on first power supply per circuit |
| `AL_Tag_Lighting Fixture (Linear Length)` | Lighting Fixture Tags | Re-tags linear fixtures after splitting (types: `Tag_Top`, `Tag_Bottom`) |

### Required Custom Parameters

**On Lighting Fixture families (type level):**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Remote Power Supply` | Yes/No (Integer) | Identifies fixtures that need remote power supplies |
| `Power` | Double (Watts) | Fixture wattage for driver sizing |
| `Manufacturer` | Text | Matched against driver manufacturer |
| `Dimming Protocol` | Text | Protocol matching (e.g., 0-10V, DMX, Phase-Cut) |
| `Voltage` | Double/Text/Integer | Operating voltage matching |

**On Lighting Fixture instances:**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Linear Power` | Double (Watts) | Instance wattage for linear fixtures |
| `Linear Length` | Double (Length) | Segment length for linear fixtures |
| `Switch ID` | Text | Read as fallback for circuit Switch ID |

**On Lighting Device families (power supply type level):**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Power` | Double (Watts) | Total driver capacity (must be integer multiple of Sub-Driver Power) |
| `Sub-Driver Power` | Double (Watts) | Wattage per sub-driver channel |
| `Maximum Fixtures` | Integer | Max fixtures per driver (0 = no limit) |
| `Manufacturer` | Text | For manufacturer-match scoring |
| `Dimming Protocol` | Text | For protocol-match scoring |
| `Voltage` | Double/Text/Integer | For voltage-match scoring |
| `Catalog Number1` | Text | Display in recommendation UI |

**On Lighting Device instances:**

| Parameter | Type | Purpose |
|-----------|------|---------|
| `Switch ID` | Text | Written by TurboDriver (e.g., X01a, X01b) |

### Other Requirements

- At least one **WireType** in the project (for wiring between stacked power supplies)
- Fixtures must have **electrical connectors**
- Active **floor plan or RCP view**
