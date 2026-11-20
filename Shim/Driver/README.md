# TurboDriver / TurboRPS

Two commands sharing the same services, models, and driver-selection algorithm (First-Fit Decreasing bin-packing over loaded driver types). Entries `DriverCommand.cs` / `RPSCommand.cs`.

Candidates are ranked in `DriverSelectionService.IsBetterCandidate` by, in order: real-over-TBD, manufacturer match, fewest physical drivers, fewest total sub-drivers, then **least overkill** — the smallest-rated unit that still packs the load. That last tie-breaker is what makes a small load land on the smallest transformer that fits (e.g. 4×7.5 W → 100 W, not 1000 W) instead of whichever type sorts first in the candidate list. It never fights the Derating Factor: a unit too small at its *derated* ceiling splits into extra sub-drivers and loses on driver count before the tie-breaker is consulted (225 W @ 80 % → 300 W, not 250 W).

### Ties and driver-family links

Every ranking criterion is a strict inequality, so on a full tie the earlier candidate (alphabetical by family, then type) used to win silently. That is fine *within* a family — TurboDriver keeps sizing among its types — but wrong when two **different** families are indistinguishable to the matcher (same Power / Sub-Driver Power, same manufacturer-match outcome, both pass Voltage and Dimming, same driver count for the load) yet are meant for different fixtures.

- **Tie** — on an unlinked circuit, the best real candidate is equalled on every criterion by a candidate from a *different* family. Computed per circuit (it depends on the load: Derating or Maximum Fixtures can break it once they change the driver count). The **TBD** placeholder never ties, and same-family equals are not ties. `DriverRecommendation.TiedCandidates` lists one representative per tied family, winner first.
- **Link** — the user's answer: a per-project **fixture type → driver family** link, created only in TurboRPS → **Driver Match**. A family, not a type, so TurboDriver still sizes among its types. A link is a **hard restriction**: only that family is considered. If none of its types fits, the circuit is **NO MATCH** naming the family (no fallback to other families or TBD). On a mixed circuit, linked fixtures must agree (two families → NO MATCH, "fix the links"); unlinked fixtures riding along defer to the link. Linking one type never reserves anything for another — an unlinked type that still ties stays a tie.
- **Storage** — `DriverLinkStorageService`: one doc-singleton ExtensibleStorage entity (schema `TurboSuiteDriverLinks`), entries `fixtureTypeUniqueId|driverFamilyUniqueId`. A link whose fixture type or family was deleted is **broken**: the matcher ignores it and Driver Match lists it flagged so it can be unlinked.
- **TurboDriver refuses a tie** — after the no-wattage / no-match aborts and **before** the circuit-info dialog or any destructive step, it shows which families tie (with catalog numbers) and which fixture types to link, then returns `Cancelled` (a freshly created circuit is discarded). No prompt-and-link shortcut: links are made in Driver Match.

## TurboDriver (DriverCommand)

Near-headless command for deploying power supplies on a per-circuit basis.

### Workflow

1. Pre-select lighting fixtures with `Remote Power Supply` type parameter enabled.
2. Run TurboDriver.
3. Command creates an electrical circuit if one doesn't exist, or uses the existing one. New circuits inherit the remembered panel default and **honor a deliberate `<None>`** (DMX/DALI etc.) exactly like TurboWire — via the shared `CircuitService`.
4. Evaluates the circuit and determines the recommended power supply type and quantity (honouring any driver-family link on the fixture types). If the fixtures can't be sized (no wattage, no matching supply, or an unlinked **tie** between driver families — see above), it stops here — **before** any dialog or destructive change.
5. **Circuit-info dialog** — the shared comment / room-override / zone dialog (`CircuitInfoService`, same as TurboWire, gated by `General > Show circuit info dialog`; the dropdown is labelled **Zone** because that is what the choice declares — see [TurboWire](../Wire/README.md#circuit-info-dialog)). Appears every run when the setting is on, prefilled so the happy path is a glance and Enter; skipped entirely when off (fully headless). This is where you add a missing comment or correct an unreliable 3D room — auto-resolution supplies the defaults, the dialog is the fallback. Nothing destructive has happened yet, so **Cancel discards a freshly created circuit cleanly**.
6. Deletes any existing power supplies on the circuit (preserving Switch ID).
7. Prompts: select an existing power supply to stack below, or press Esc to pick a bare point.
8. Places power supplies in a column (9.5" apart at 1/4" = 1'-0", scaled by `K = view.Scale / 48` of the active view so the stack keeps its printed spacing at other scales — `PlanScale`, see CLAUDE.md), connects to circuit, sets suffixed Switch IDs (e.g., X01a, X01b), and tags each with SwitchID and Switchleg tags. New supplies are **hosted on the active plan's level** with **Elevation from Level set explicitly** to the view's display plane (RCP top / floor-plan bottom) so they land visible and in-range — see CLAUDE.md "`NewFamilyInstance` ignores the placement Z" for why the explicit set (computed off `Level.ProjectElevation`, not `Level.Elevation`) is required.
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
   - **NO MATCH** — no real driver fits (or a link restricts it to a family that can't serve it, or two
     links on the circuit conflict — the detail pane says which).
   - **TIE** — unlinked, and two or more driver families serve it equally (see "Ties and driver-family
     links"). Checked before NEW, since TurboDriver refuses a tie. Amber, counted as an issue, not
     batch-selectable; the Note names the families and the detail pane lists them (marking the placed one)
     plus the fixture types to link, each with how many circuits carry it. Resolve it by linking in
     **Driver Match** (or defer the circuit). A tie's deferral signature also records the tied families.
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

REBUILD/NEW/NO MATCH/TIE rows are not auto-fixed — their checkbox is disabled and a hint points to TurboDriver.
A STALE row whose recommendation re-splits a linear fixture also shows an info-only "linear cut-list
changed — re-run TurboDriver to re-split" note; the in-place swap still fixes the driver *type*, but the
physical tape segmentation is a TurboDriver job.

Requires at least one loaded Lighting Device family type with valid `Power` and `Sub-Driver Power` parameters.

### Driver Match tab

A diagnostic **tab in the TurboRPS window** for the recurring annoyance of building a driver
family that TurboDriver then refuses to pair with a fixture — and the home of **driver-family links**.
It's the design-time half of the same driver-correctness concern TurboRPS owns: the **Circuits** tab
answers "are my *placed* drivers still right?", the **Driver Match** tab answers "*can* this driver family
serve this fixture type at all?". Pick an **RPS fixture type** and a **driver family** from two dropdowns
(the project's loaded types) and the grid shows **one row per type in the family** with a column per
check (Valid · Voltage · Dimming · Mfr · Capacity · Notes) — no selection or placement needed, so it works
on a family you just built and reloaded. The verdict bar reads "✓ N of M types can serve L1" or names the
most common blocking field. The window opens even with no RPS circuits (so the tab is usable mid
family-build); it only refuses when *both* tabs would be empty.

**Linking.** Under the verdict bar, the link bar shows the selected fixture type's link and offers
**Link to family** / **Change link** (enabled when at least one type in the family matches and the family
isn't TBD) and **Unlink**. The **Links in this project** expander lists every link — broken ones flagged
— each with its own Unlink. Every link change re-collects the tab and **auto-rescans the Circuits tab**
(both ride the one FIFO work queue, so the rescan runs after the save).

Composed, not merged: a thin `TurboRpsShellViewModel` exposes `RpsMainViewModel` (`Circuits`) and
`DriverMatchViewModel` (`Match`) as the two tabs' DataContexts, and both share the one `RevitWorkQueue`
the command creates. The shell's only wiring is `Match.LinksChanged → Circuits.RequestRescan()`. The
tab's UI is `Shim/Driver/Views/DriverMatchView.xaml`.

- **Dropdowns.** Driver families = families of Lighting Devices that are driver-shaped: no `TurboSuite Role` (drivers are
  roleless) or the `DriverPlaceholder` (TBD), and **not** a DMX decoder (`DMX Channels > 0`). Keypads,
  switches, receptacles, shade motors, hybrid repeaters (all carry an explicit non-driver role) and
  decoders drop out — but an *incomplete* real driver stays listed (it's roleless) so the grid can show
  why it isn't valid. Fixtures = Lighting Fixtures with `Remote Power Supply` = yes (the only ones
  TurboDriver sizes a driver for); a fixture missing from the list simply hasn't that box checked.
- **The checks mirror the real matcher.** Hard gates (a failure blocks the pair): **valid driver**
  (`Power` is a positive whole multiple of `Sub-Driver Power` — shared with the candidate-pool collector
  via `Core/Driver/Services/DriverValidity.cs`), **Voltage**, **Dimming Protocol** (both: driver's must be
  non-blank and equal the fixture's, case-insensitive — blank is **not** a wildcard). Soft: **Manufacturer**
  (a mismatch still matches, just loses the ranking). **Wattage** is info-only — a linear fixture carries
  no type-level wattage, so it never gates. The **TBD** placeholder is the one wildcard: it matches
  anything. `Core/Driver/Services/DriverMatchDiagnostic.cs` is the pure comparison (oracle-tested;
  `CompareFamily` runs the per-pair `Compare` over each type); it must stay in **lockstep** with
  `DriverSelectionService.EvaluateCandidate` — add a hard-gate check here whenever a new hard filter is
  added there. A link is a candidate-pool restriction, not a per-pair gate, so it isn't a check here.
- **Modeless; the only write is a link.** Type lists and links are collected once on open (and on
  **Reload**, for the edit-reload-recheck loop) through the shared `RevitWorkQueue`; every dropdown change
  recomputes purely in-memory. Link / Unlink lease the link store and save it in one transaction.

## Dependencies

### Required Tag Families

Found by their `TurboSuite Role` type parameter, not by family name (see `Core/Shared/Constants/Roles.cs`):

| Role | Category | Purpose |
|------|----------|---------|
| `SwitchIdTag` | Lighting Device Tags | Tags Switch ID on placed power supplies |
| `DeviceSwitchlegTag` | Lighting Device Tags | Switchleg tag on first power supply per circuit |
| `LinearTag` | Lighting Fixture Tags | Re-tags linear fixtures after splitting (types: `Tag_Top`, `Tag_Bottom`), at TurboTag's view-scaled linear offset (`TagGeometry.LinearOffsetFeet`) |

The TBD placeholder driver is likewise identified by Role `DriverPlaceholder` (was the `AL_RPS_TBD` family-name substring).

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

Device instances carry `Switch ID` (Text), written by TurboDriver (e.g. `X01a`, `X01b`; see step 8).

### Other Requirements

- At least one **WireType** in the project (for wiring between stacked power supplies)
- Fixtures must have **electrical connectors**
- Active **floor plan or RCP view**
