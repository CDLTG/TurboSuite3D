# TurboHighlight

One keystroke toggles the room highlight in the active plan view. Lives in the **Scripts** pulldown (Tools panel, `App/TurboSuiteApplication.cs`) as **Toggle Room Highlight**; meant to be driven by a user-assigned keyboard shortcut. One command, no Core/VM split — all logic in `HighlightCommand.cs`. Each press is one undo entry; success is silent (the view is the feedback).

## Behavior

| Job | Detected by | Press when… | Result |
|-----|-------------|-------------|--------|
| **3D** | any `RevitLinkInstance` (loaded or not) | Spaces off (any subcategory state) | Spaces on, Color Fill / Interior / Reference all on |
| | | Spaces on, subcategories all off | subcategories all on |
| | | Spaces on, subcategories mixed or all on | subcategories all off |
| **2D** | no RVT link | filter visible | `Highlight - Rooms` visibility off |
| | | filter hidden | visibility on |
| | | filter not on the view | filter added, visible |

Every 3D press leaves Spaces on. Spaces off counts as "subcategories all off" because that's what the view shows — reading the hidden subcategories' own checkboxes there would make the press look like it did nothing.

The 2D branch forces **Enable Filter** on every press. The filter definition comes from the firm project template; a project without it gets a message and no change.

## Gotchas

- **View template = refusal.** Firm templates force the V/G values, and the workflow is to *unassign* the template before editing. A template still on the view is treated as intentional, so the command reports it and changes nothing — it never edits the template (that would flip every view using it).
- **3D/2D seam differs from TurboSetup.** TurboSetup needs a *loaded* link (it reads the link doc); this only toggles V/G, so an unloaded arch link still counts as 3D.
- **Subcategory ids (spike-confirmed, Revit 2025).** The V/G names map to `OST_MEPSpaceColorFill`, `OST_MEPSpaceInteriorFillVisibility` ("Interior"), and `OST_MEPSpaceReferenceVisibility` ("Reference"). The look-alike `OST_MEPSpaceInteriorFill` / `OST_MEPSpaceReference` resolve to **no category** — don't swap them in.
- **Color Fill** only shows anything when the view has a Space color scheme assigned; toggling it is harmless otherwise.
