# Driver Module

Contains two commands that share the same services, models, and driver selection algorithm.

## TurboDriver (DriverCommand)

Near-headless command for deploying power supplies on a per-circuit basis.

### Workflow

1. Pre-select lighting fixtures with `Remote Power Supply` type parameter enabled.
2. Run TurboDriver (suggested shortcut: TD).
3. Command creates an electrical circuit if one doesn't exist, or uses the existing one. New circuits inherit the remembered panel default and **honor a deliberate `<None>`** (DMX/DALI etc.) exactly like TurboWire — via the shared `CircuitService`.
4. Evaluates the circuit and determines the recommended power supply type and quantity. If the fixtures can't be sized (no wattage, or no matching supply), it stops here — **before** any dialog or destructive change.
5. **Circuit-info dialog** — the shared comment / room-override / panel dialog (`CircuitInfoService`, same as TurboWire, gated by `General > Show circuit info dialog`). Appears every run when the setting is on, prefilled so the happy path is a glance and Enter; skipped entirely when off (fully headless). This is where you add a missing comment or correct an unreliable 3D room — auto-resolution supplies the defaults, the dialog is the fallback. Nothing destructive has happened yet, so **Cancel discards a freshly created circuit cleanly**.
6. Deletes any existing power supplies on the circuit (preserving Switch ID).
7. Prompts: select an existing power supply to stack below, or press Esc to pick a bare point.
8. Places power supplies in a column (9" apart), connects to circuit, sets suffixed Switch IDs (e.g., X01a, X01b), and tags each with SwitchID and Switchleg tags. New supplies are **hosted on the active plan's level** and their **Elevation from Level is set explicitly** (to the view's display plane — RCP top / floor-plan bottom) so they land visible and in-range. This is authoritative: `NewFamilyInstance` does not reliably honor the placement Z for these level-based families (it inherits the family's sticky elevation default), and `Level.ProjectElevation` — not `Level.Elevation`, which a survey/relocated elevation base can inflate — is the frame the elevation is computed in.
9. Re-selects the circuit's lighting fixtures on exit so the user can immediately assign them to switches without re-picking.

## TurboRPS (RPSCommand)

Modeless **staleness dashboard + batch in-place driver-type corrector** across all RPS circuits.
TurboDriver owns the physical deployment; TurboRPS re-runs the driver selection on what's already
placed, flags circuits whose driver selection has gone stale (typically after tape wattage changes),
and fixes the common case in place.

### How It Works

1. Scans all circuits with at least one Lighting Fixture that has the `Remote Power Supply` type parameter enabled.
2. For each circuit, reads fixture wattage, manufacturer, dimming protocol, and voltage, and re-runs the
   same recommendation engine TurboDriver uses (First-Fit Decreasing bin-packing over loaded driver types).
3. Compares the placed supplies against the fresh recommendation and assigns a status (see `StaleClassifier`):
   - **OK** — placed type == recommended type, same physical driver count.
   - **STALE** — recommended type differs but is the **same family + same driver count**; fixable in place.
   - **REBUILD** — needs delete+re-place (→ TurboDriver): physical count changed, a **different family**
     is recommended (cross-family `Symbol` swap throws), or the placed supplies are mixed/ambiguous.
   - **NEW** — RPS fixtures but no supplies placed yet.
   - **NO MATCH** — no real driver fits.
   - **DMX** — the circuit is DMX-decoder-controlled (a decoder device — `OST_LightingDevices` with
     `DMX Channels` > 0 — is wired on it). Decoders are a parallel class of power supply TurboRPS doesn't
     size, so the circuit is flagged green ("present & wired") with no wattage recommendation; decoder
     sizing/packing belongs to TurboDMX. Decoders are also excluded from the driver candidate pool, so
     they never inflate the placed count or feed a recommendation.
   - **DEFERRED** — a user has intentionally accepted a "mathematically wrong" config (see Usage); the
     real verdict is masked to neutral and excluded from the issue counts.
   - **REVIEW** — a deferred circuit whose config has drifted since it was deferred, re-surfaced for review.

### Usage

1. Run TurboRPS (no pre-selection needed — scans the entire project). The modeless window stays open
   alongside Revit edits.
2. Each circuit is a grid row with its status, the placed-driver Switch IDs (the **Number** column,
   e.g. `X07a, X07b`), current vs. recommended driver, and a detail pane (recommended sub-driver
   packing + grouped fixtures) for the selected row. The search box live-filters on circuit number
   **or** Switch ID (substring, so `X07` matches `X07a`/`X07b`).
3. Check the **STALE** rows (only those are selectable) — or **Select all stale** — and click
   **Re-run selected**. Each placed driver is retyped via an in-place `FamilyInstance.Symbol` swap in a
   **single transaction (one undo step)**, preserving location, Workset, the Plan Visibility param,
   wiring, tags, instance Switch IDs, and the manual switch-system memberships.
4. **Rescan** re-collects + reclassifies without closing (after editing wattage with the window open).
   **Select in Project** selects + zooms the selected circuit's member elements.
5. **Defer a circuit** — right-click a row → **Defer circuit** for systems that are intentionally
   "wrong" (a knowingly non-optimal driver config kept for external reasons). The row goes neutral
   (**DEFERRED**), drops out of the issue counts and batch-correct, and is hidden by "Show only issues".
   The flag is stored in ExtensibleStorage **on the circuit element**, so it travels with the model
   (all users see it) and auto-clears if the circuit is deleted/rewired. The config is snapshotted at
   defer time; if the circuit later drifts the row resurfaces as **REVIEW** so a stale deferral can't
   hide a new problem. Right-click → **Clear deferral** to undo.

REBUILD/NEW/NO MATCH rows are not auto-fixed — their checkbox is disabled and a hint points to TurboDriver.
A STALE row whose recommendation re-splits a linear fixture also shows an info-only "linear cut-list
changed — re-run TurboDriver to re-split" note; the in-place swap still fixes the driver *type*, but the
physical tape segmentation is a TurboDriver job.

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
| `Dimming Protocol` | Text | Protocol matching (e.g., 0-10V, ELV, DMX) |
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
