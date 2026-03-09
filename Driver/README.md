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
