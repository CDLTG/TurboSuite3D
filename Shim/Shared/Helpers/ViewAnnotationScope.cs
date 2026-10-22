using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Shared.Helpers;

/// <summary>
/// The views whose annotation a command's re-run cleanup may delete: the placement view's primary
/// view plus all of its dependents (Revit shares annotation across that family natively), or the view
/// alone when it has neither. Tags and wires are view-owned, so a re-run must replace only its own
/// view's output — a doc-wide search also deletes the same fixture's tags/wires in other plans (e.g.
/// the 1/4" plan when re-running in a 1/8" duplicate). Matching the whole primary/dependent family
/// holds whichever id Revit reports as <see cref="Element.OwnerViewId"/> for annotation created in a
/// dependent view.
/// </summary>
public sealed class ViewAnnotationScope
{
    private readonly HashSet<ElementId> _viewIds;

    public ViewAnnotationScope(View view)
    {
        View primary = view.Document.GetElement(view.GetPrimaryViewId()) as View ?? view;
        _viewIds = new HashSet<ElementId> { view.Id, primary.Id };
        foreach (ElementId id in primary.GetDependentViewIds())
            _viewIds.Add(id);
    }

    /// <summary>True when the element is owned by the placement view or its primary/dependent family.</summary>
    public bool Owns(Element element) => _viewIds.Contains(element.OwnerViewId);
}
