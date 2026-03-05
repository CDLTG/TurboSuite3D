# TurboDriver

Manages lighting device (driver/power supply) family types for circuits with **Remote Power Supply** enabled.

## How It Works

1. Scans all circuits with at least one Lighting Fixture that has the `Remote Power Supply` type parameter enabled.
2. For each circuit, reads fixture wattage, manufacturer, dimming protocol, and voltage.
3. Evaluates all loaded driver family types (must have both `Power` and `Sub-Driver Power` parameters; `Power` must be an integer multiple of `Sub-Driver Power`).
4. Runs **First-Fit Decreasing bin-packing** to assign fixtures to sub-drivers, recursively splitting high-wattage fixtures across multiple slots.
5. Recommends the best driver type per circuit, prioritizing: manufacturer match, fewest physical drivers, fewest sub-drivers.

## Usage

1. Run TurboDriver (no pre-selection needed — scans the entire project).
2. The window shows each qualifying circuit with its fixtures, wattage, and recommendation.
3. Use the **dropdown** on each device group to change the family type — changes are written to Revit immediately.
4. Total power supply count is displayed. Warnings appear when no suitable driver is found.

Requires at least one loaded Lighting Device family type with valid `Power` and `Sub-Driver Power` parameters.
