# TurboBubble

Places a switchleg tag and stub arc wire on a single lighting or electrical fixture. Entry `BubbleCommand.cs`; per-family-kind placement is a strategy set under `Placement/` (`IPlacementCalculator` → `Horizontal` / `VerticalFace` / `Chandelier` / `PictureLight` / `LineBased`), fixture routing in `Services/FixtureAnalysisService.cs`, tag/wire emission in `Services/TagPlacementService.cs` + `WirePlacementService.cs`. Offset lengths are in `Core/Bubble/Constants/BubbleGeometry.cs`; scale-independent constants (type names, angles, epsilons) are in `BubbleConstants.cs`. Floor plan or RCP; needs a WireType and electrical connectors on the fixture. Existing switchleg tags and orphaned stub wires **in this view** (and its primary/dependent views, which share annotation) are cleaned up before placement; the same fixture's tags and wires in other plans are left alone (`ViewAnnotationScope`).

## Plan scale

Every offset is a 1/4" = 1'-0" reference value scaled by `K = view.Scale / 48` of the active view (`PlanScale`, see CLAUDE.md). `BubbleCommand` builds one `BubbleGeometry` per run and passes it to the calculators and services. At 3/16" or 1/8" the tag, elbow and wire stubs sit around the (Revit-scaled) fixture symbol exactly as they do at 1/4", at the same printed size. At 1/4" the output is unchanged. Set the view scale **before** placing; after a scale change, re-run TurboBubble on the fixture, which replaces its switchleg tag and stub wire. Measured symbol and tag extents already come back at view scale, so only the constants added to them carry `K`. The fixture `Scale Factor` (wall sconces) still multiplies on top. `BubbleGeometryTests` pins every member to its pre-scale value at 1/4" and to linear scaling with `K`.

**Exception: ceiling fans.** Ceiling-fan families deliberately draw their symbol as model geometry (no nested annotation), so the fan shows its true size at every scale. The tag, the arc control point (V2) and the elbow still scale, since they stand off in paper space around the scaled tag. The wire's start vertex (V1) is seated on the fan base, so its offset (`BubbleConstants.CeilingFanV1*`) is real-world and does not scale.

## Placement model

- **Lighting fixture** — pick the fixture's **existing tag** (not the fixture), then a second point sets the tag side (left/right). A `Remote Power Supply` type routes to the directional remote tag (`Switchleg Left` / `Switchleg Right`) instead of the standard one.
- **Electrical fixture** — pick the fixture directly; a direction point places the tag. Vertical families (exhaust, fireplace igniter) place up/down, others left/right.

### Linear point fixtures (light bars)

For **linear** point fixtures (a LocationPoint family whose long plan extent is ≥ 3× its short one — the same test TurboWire uses via `GeometryHelper.TryGetLinearLongAxis`), the wire's connector-end vertex is anchored at the bar's **end** rather than its center, so the switchleg arc springs from the end the tag sits off — no manual v1 drag. The end is the long-axis end toward the wire (nearest `Vertex2`, i.e. the side clicked); the tag and the `Vertex2`/`Vertex3` arc are unchanged, and the electrical connection is untouched (a display-only vertex move). Non-linear fixtures (square downlights) keep the center anchor. Horizontal (ceiling/floor) path only — tape, wall-sconce, chandelier, and electrical-fixture paths are unaffected.

At smaller view scales with the `Symbol Length` divisor re-mapped (e.g. ÷96 at 1/8"), a short bar can stop counting as linear and keep the center anchor (see TurboWire's README, "Known limitation at smaller view scales"). This is accepted: Revit clips the wire at the nested annotation's edge line, so it still reads as leaving from the end in most configurations.

### Custom-length linear fixtures (line-based)

A line-based family with a boxed plan symbol (`Symbol Length` and `Symbol Width` both set, `GeometryHelper.IsBoxedLineBasedFixture`) and **no** `Remote Power Supply` bubbles exactly like a linear point fixture: it takes `HorizontalPlacementCalculator`, so the tag sits past the bar's end and the wire leaves from that end. Your click picks which end. Because the source is a `LinearTag` or run tag, the frame comes from the fixture's line rather than the tag's `Orientation`/`Angle`, and the tag only picks the side of the line.

The same family **on a `Remote Power Supply` is functionally tape** and stays on `LineBasedPlacementCalculator`, as do tape itself and wall-hosted or plumb runs. TurboTag treats these hybrid families as line-based in both modes.

**Authoring requirement:** the wire end is measured as `connector ± half length`, so the connector must sit at the line's midpoint unless the type uses a remote power supply, where it belongs at the feed end like tape. Drive the connector's position from the line's start with a `Connector Offset` formula: `if(Remote Power Supply, 0, Length / 2)`. TurboWire's end-to-end routing relies on the same centered connector. The code reads the connector's actual position, not this parameter (TurboMask uses it only as a fallback for a family with no connector).

### Chandeliers / decorative pendants

`AL_Decorative_Pendant (Hosted)`, `AL_Decorative_Pendant`, `Z_Chandelier` use diagonal-corner switchleg placement (one of four corners picked by combining the type tag's side with the user click), with the wire arc curving away from the type tag.

### Picture lights

Fixtures whose `TurboSuite Role` is `PictureLight` route to a dedicated `PictureLightPlacementCalculator` **ahead of** the vertical-face/horizontal branches, so both the 2D and 3D picture-light families place identically and sconces/mirrors on `VerticalFacePlacementCalculator` are untouched. A picture light's plan symbol is **symmetric along the wall** but extends **entirely away from the wall** (the origin/connector sits on the wall-side edge) — a spike confirmed the 2D and 3D families are identical in wall-normal terms, and both yield a usable wall normal (3D via Hand×Facing, 2D via the facing fallback).

The calculator works in the wall frame (X along wall, Y = wall normal into the room) and measures the **actual** room-side depth via `GeometryHelper.GetSymbolExtentInDirection`, so the bubble stands off past the bar into open room (the "2D look") rather than assuming a centered symbol. v1 anchors at the bar end (scales with bar length); the wire arcs down to a bubble cleared past the measured depth. Offsets are in `BubbleGeometry.PictureLight*`, tuned from a hand-drawn ideal. **Every perpendicular vertex is anchored to the measured room-side edge** — the bubble/elbow via `PictureLightTagClearanceFt` (`roomDepth + clearance`), and the two wire vertices via `PictureLightWireEndInsetFt`/`PictureLightWireMidInsetFt` (`roomDepth - inset`). So the whole switchleg figure is a rigid shape that simply **translates outward for a deeper symbol**: a picture light that extends further into the room keeps the same arc, now springing from its own room-side edge. The insets were derived from the reference family's spiked depth (4.487″) to reproduce the tuned look byte-for-byte.

## Required families / parameters

Tag/detail families are found by their `TurboSuite Role` type parameter, not by name (see `Core/Shared/Constants/Roles.cs`):

| Tag/detail Role | Types required |
|-------------|----------------|
| `FixtureSwitchlegTag` | default type |
| `RemoteSwitchlegTag` | `Switchleg Left`, `Switchleg Right` |
| `ElectricalSwitchlegTag` | default type |
| `LinearFeedTag` / `LinearFeedDetail` | tag: `SwitchID` type (static driver-tag path) |

| Parameter | On | Purpose |
|-----------|----|---------|
| `Remote Power Supply` | Lighting Fixture type | Selects remote vs. standard switchleg tag |
| `Scale Factor` | Fixture instance | Scales wire offset distances for wall sconces (a size knob, composed with the view-scale `K`, not a substitute for it) |
| `Symbol Length`, `Symbol Width` | Line-based fixture | Both above zero marks a boxed fixture rather than tape, so it can bubble like a linear fixture |

## Recognized electrical fixture families

Special vertical / ceiling-fan / sconce offsets are triggered by the fixture's `TurboSuite Role` (see `Core/Shared/Constants/Roles.cs`): `ExhaustFan` and `FireplaceIgniter` (electrical-vertical switchleg — the one intentional many-to-one), `CeilingFan` (fixed vertical, no flip prompt), and `Sconce` (special wire offset). Chandeliers use Role `Chandelier`.
