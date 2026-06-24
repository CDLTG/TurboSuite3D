# TurboSnoop

Read-only "which Visibility/Graphics checkbox do I uncheck?" reporter for linked architectural geometry.

Clearance, path, and egress lines (and other content) often ride inside deeply nested families in a linked model, and finding the right **Category → Subcategory** to uncheck in **VG → RVT Links → Custom** by hand is slow trial-and-error. TurboSnoop picks one linked family and lists every VG checkbox its geometry draws under, so you know exactly which box to clear.

**Suggested shortcut:** `TS` (replaces the unused Revit default for Toposolid → Smooth Shading)

## Usage

1. Run TurboSnoop.
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
