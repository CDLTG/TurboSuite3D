# TurboSnoop

Read-only reporter for the two "what is this connected to?" questions Revit hides. **Selection-aware, one button, two branches:**

- **Nothing selected → pick a linked family → VG report** — "which Visibility/Graphics checkbox do I uncheck?"
- **Your own element selected → host report** — "what is this element hosted to?"

**Suggested shortcut:** `TS` (replaces the unused Revit default for Toposolid → Smooth Shading)

## Host report — "what am I hosted to?"

Revit's Properties shows a link-hosted element only as *hosted to `<the link>`* — never *which* element in that link. That gap bites: a keypad can land face-hosted to a linked **casework** family (authored square, geometry on one side, sitting flush on the real wall) with no visual tell, which both misroutes wall-normalization and leaves the keypad **orphaned** the moment that volatile casework is deleted or reworked.

Select one of your own families and run TurboSnoop: it resolves the actual host through the link and names it, with a **risk tier** — *stable* (linked Walls/Ceilings/Floors/Roofs), *churn risk* (linked Casework/Furniture/Generic Models/Stairs/Doors — deletion orphans you), *intentional* (hosted to your own in-model element, e.g. a track fixture on its track), *orphaned* (link-hosted but the host no longer resolves), or *unhosted* (2D/free). The tier boundaries live in one editable set in `Core/Host/HostRiskClassifier.cs`.

The resolver is `Core/Host/` (pure result + classifier, unit-tested) plus `Shim/Shared/Services/HostResolutionService.cs` (the Revit walk). Its `ResolveAll` sweep is a deliberate stub — a **future full-model host audit** (report home TBD) will reuse the same resolver; only the single-element path ships today. Like the VG branch, the host report is **read-only** — it names the host and the risk; re-hosting stays a manual act.

**Why selection-aware, not one pick:** no `PickObject` mode accepts both a host-doc element and a nested linked sub-element, so the split is "own element already selected → host; else pick into a link → VG." To reach the VG path, deselect first.

## VG report — "which checkbox do I uncheck?"

Clearance, path, and egress lines (and other content) often ride inside deeply nested families in a linked model, and finding the right **Category → Subcategory** to uncheck in **VG → RVT Links → Custom** by hand is slow trial-and-error. TurboSnoop picks one linked family and lists every VG checkbox its geometry draws under, so you know exactly which box to clear.

### Usage

1. Run TurboSnoop with **nothing selected**.
2. Pick a linked architectural family in the view (press **Escape** to cancel before anything opens).
3. A modeless window lists the **Category → Subcategory** Visibility/Graphics checkboxes the family's geometry draws under, split into two sections:
   - **Model geometry** — always drawn, collected in one pass.
   - **View-dependent / annotation** — detail items, masking, and symbolic lines that are visibility-filtered per view (collected by sweeping every plan view and unioning the result).
4. Find the bulleted leaf checkbox in your **VG → RVT Links → Custom** dialog and uncheck it. The window stays open and non-activating, so Revit's VG/VV keybind keeps working while you do.

## Deliberately read-only — no Apply

TurboSnoop **names** the checkbox; it does not flip it. There is no Revit API path to do so:

- `RevitLinkGraphicsSettings` exposes only whole-link knobs (no per-category setter), and its `Custom` mode isn't even settable in the Revit 2024 API.
- Host-view `View.SetCategoryHidden` can only drive categories that exist in the **host** document — the link-defined subcategories this tool exists to surface aren't reachable.

So the single uncheck is left to you, by design. See the design/rejected-alternatives rationale in the `SnoopCommand.cs` and `LinkedGeometryTreeBuilder.cs` headers.

## Dependencies

| Requirement | Purpose |
|-------------|---------|
| A **loaded** RVT link with a pickable family | Source of the geometry being reported |
| An active **plan view** | The annotation sweep iterates the document's `ViewPlan`s |

## Notes

- The picked element must resolve to a `RevitLinkInstance` with its linked document loaded; otherwise the command reports and exits.
- Nested **non-shared annotation** (e.g. a Detail Item nested in a sink family) has no element or reference identity, so it can only be reached by reading the graphics style off rendered geometry — which is exactly how this builder works.
- Picking a fresh family replaces the previous report; only one TurboSnoop window is open at a time.
