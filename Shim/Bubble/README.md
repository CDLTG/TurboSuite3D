# TurboBubble

Places a switchleg tag and stub arc wire on a single lighting or electrical fixture. Entry `BubbleCommand.cs`; per-family-kind placement is a strategy set under `Placement/` (`IPlacementCalculator` → `Horizontal` / `VerticalFace` / `Chandelier` / `PictureLight` / `LineBased`), fixture routing in `Services/FixtureAnalysisService.cs`, tag/wire emission in `Services/TagPlacementService.cs` + `WirePlacementService.cs`, offset constants in `Core/Bubble/Constants/BubbleConstants.cs`. Floor plan or RCP; needs a WireType and electrical connectors on the fixture. Existing switchleg tags and orphaned stub wires are cleaned up before placement.

## Placement model

- **Lighting fixture** — pick the fixture's **existing tag** (not the fixture), then a second point sets the tag side (left/right). A `Remote Power Supply` type routes to the directional remote tag (`Switchleg Left` / `Switchleg Right`) instead of the standard one.
- **Electrical fixture** — pick the fixture directly; a direction point places the tag. Vertical families (exhaust, fireplace igniter) place up/down, others left/right.

### Linear point fixtures (light bars)

For **linear** point fixtures (a LocationPoint family whose long plan extent is ≥ 3× its short one — the same test TurboWire uses via `GeometryHelper.TryGetLinearLongAxis`), the wire's connector-end vertex is anchored at the bar's **end** rather than its center, so the switchleg arc springs from the end the tag sits off — no manual v1 drag. The end is the long-axis end toward the wire (nearest `Vertex2`, i.e. the side clicked); the tag and the `Vertex2`/`Vertex3` arc are unchanged, and the electrical connection is untouched (a display-only vertex move). Non-linear fixtures (square downlights) keep the center anchor. Horizontal (ceiling/floor) path only — line-based, wall-sconce, chandelier, and electrical-fixture paths are unaffected.

### Chandeliers / decorative pendants

`AL_Decorative_Pendant (Hosted)`, `AL_Decorative_Pendant`, `Z_Chandelier` use diagonal-corner switchleg placement (one of four corners picked by combining the type tag's side with the user click), with the wire arc curving away from the type tag.

### Picture lights

`PictureLightFamilies` (`Z_Picture Light` 2D, `AL_Decorative_Picture Light (Hosted)` 3D) route to a dedicated `PictureLightPlacementCalculator` **ahead of** the vertical-face/horizontal branches, so both families place identically and sconces/mirrors on `VerticalFacePlacementCalculator` are untouched. A picture light's plan symbol is **symmetric along the wall** but extends **entirely away from the wall** (the origin/connector sits on the wall-side edge) — a spike confirmed the 2D and 3D families are identical in wall-normal terms, and both yield a usable wall normal (3D via Hand×Facing, 2D via the facing fallback).

The calculator works in the wall frame (X along wall, Y = wall normal into the room) and measures the **actual** room-side depth via `GeometryHelper.GetSymbolExtentInDirection`, so the bubble stands off past the bar into open room (the "2D look") rather than assuming a centered symbol. v1 anchors at the bar end (scales with bar length); the wire arcs down to a bubble cleared past the measured depth. Offsets are in `BubbleConstants.PictureLight*`, tuned from a hand-drawn ideal. **Every perpendicular vertex is anchored to the measured room-side edge** — the bubble/elbow via `PictureLightTagClearanceFt` (`roomDepth + clearance`), and the two wire vertices via `PictureLightWireEndInsetFt`/`PictureLightWireMidInsetFt` (`roomDepth - inset`). So the whole switchleg figure is a rigid shape that simply **translates outward for a deeper symbol**: a picture light that extends further into the room keeps the same arc, now springing from its own room-side edge. The insets were derived from the reference family's spiked depth (4.487″) to reproduce the tuned look byte-for-byte.

## Required families / parameters

| Tag family | Types required |
|-------------|----------------|
| `AL_Tag_Lighting Fixture (Switchleg)` | default type |
| `AL_Tag_Lighting Fixture (Remote Switchleg)` | `Switchleg Left`, `Switchleg Right` |
| `AL_Tag_Electrical Fixture (Switchleg)` | default type |

| Parameter | On | Purpose |
|-----------|----|---------|
| `Remote Power Supply` | Lighting Fixture type | Selects remote vs. standard switchleg tag |
| `Scale Factor` | Fixture instance | Scales wire offset distances for wall sconces |

## Recognized electrical fixture families

Trigger vertical / ceiling-fan / sconce offsets: `AL_Electrical Fixture_Exhaust (Hosted)` / `Exhaust`, `AL_Electrical Fixture_Fireplace Igniter` / `Fireplace Igniter`, `AL_Electrical Fixture_Ceiling Fan (Hosted)` / `Ceiling Fan`, `AL_Decorative_Wall Sconce (Hosted)` (special wire offset).
