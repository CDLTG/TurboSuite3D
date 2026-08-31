#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Setup.Models;
using TurboSuite.Setup.Services;
using TurboSuite.Setup.ViewModels;
using TurboSuite.Setup.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Setup;

/// <summary>
/// TurboSetup — automates new-project setup: copy levels from the linked architectural model,
/// create Floor Plan + RCP views per level, assign firm view templates, and wire each view's
/// link graphics to a chosen architectural view. v1 handles the 3D (RVT-linked) workflow only;
/// 2D (CAD-linked) projects get a clean "not yet supported" message.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class SetupCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc0 = commandData.Application.ActiveUIDocument;
        Document doc0 = uidoc0?.Document;
        if (doc0 == null)
        {
            TaskDialog.Show("TurboSetup", "No active document found.");
            return Result.Failed;
        }

        // ── Landing menu: a suite-styled launcher window routes to the setup wizard or the
        //    space-naming action (replacing the old native TaskDialog command-link menus). ──
        var landing = new TurboSetupLandingWindow();
        new WindowInteropHelper(landing) { Owner = commandData.Application.MainWindowHandle };
        landing.ShowDialog();

        switch (landing.Choice)
        {
            case TurboSetupLandingWindow.SetupChoice.ProjectSetup:
                return RunProjectSetup(commandData, ref message, elements);
            case TurboSetupLandingWindow.SetupChoice.NameSpacesBlankOnly:
                return RunNameSpaces(doc0, force: false);
            case TurboSetupLandingWindow.SetupChoice.NameSpacesForce:
                return RunNameSpaces(doc0, force: true);
            default:
                return Result.Cancelled;
        }
    }

    private Result RunProjectSetup(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;

            if (doc == null)
            {
                TaskDialog.Show("TurboSetup", "No active document found.");
                return Result.Failed;
            }

            IntPtr owner = commandData.Application.MainWindowHandle;

            // ── Project-type detection (the 3D/2D seam) ──
            var allLinks = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            var loadedLinks = allLinks.Where(l => l.GetLinkDocument() != null).ToList();

            if (loadedLinks.Count == 0)
            {
                // 2D arm (not designed yet) — or an arch link is present but unloaded.
                if (allLinks.Count > 0)
                {
                    TaskDialog.Show("TurboSetup",
                        "An architectural link is present but unloaded.\n\n" +
                        "Reload the link (Manage → Manage Links) and run TurboSetup again.");
                }
                else
                {
                    TaskDialog.Show("TurboSetup",
                        "TurboSetup currently supports 3D RVT-linked projects.\n\n" +
                        "2D drafting setup is coming in a future release.");
                }
                return Result.Cancelled;
            }

            // ── Pre-flight: firm view templates must exist (fail fast) ──
            ElementId floorTemplateId = ViewGenerationService.FindTemplateId(doc, SetupConstants.FloorPlanViewTemplateName);
            ElementId rcpTemplateId = ViewGenerationService.FindTemplateId(doc, SetupConstants.RcpViewTemplateName);
            if (floorTemplateId == null || rcpTemplateId == null)
            {
                var missing = new List<string>();
                if (floorTemplateId == null) missing.Add($"\"{SetupConstants.FloorPlanViewTemplateName}\"");
                if (rcpTemplateId == null) missing.Add($"\"{SetupConstants.RcpViewTemplateName}\"");
                TaskDialog.Show("TurboSetup",
                    $"Required view template(s) not found: {string.Join(", ", missing)}.\n\n" +
                    "Open a project from the firm template (or load these templates) before running TurboSetup.");
                return Result.Cancelled;
            }

            // ── Stage 1: link + level picker ──
            var linkOptions = loadedLinks
                .Select(l => new TurboSetupLinkLevelViewModel.LinkOption(l.Id, l.Name, l.GetLinkDocument()))
                .ToList();

            var stage1Vm = new TurboSetupLinkLevelViewModel(linkOptions);
            var stage1 = new TurboSetupLinkLevelWindow(stage1Vm);
            new WindowInteropHelper(stage1) { Owner = owner };
            stage1.ShowDialog();

            if (!stage1Vm.Confirmed)
                return Result.Cancelled;

            var selectedRows = stage1Vm.GetSelectedLevels(out int mainIndex);
            if (selectedRows.Count == 0)
                return Result.Cancelled;

            RevitLinkInstance linkInstance = (RevitLinkInstance)doc.GetElement(stage1Vm.SelectedLink.InstanceId);
            Document linkDoc = stage1Vm.SelectedLink.LinkDocument;
            Transform linkTransform = linkInstance.GetTotalTransform();

            // ── Indexing + planned views ──
            var indices = LevelIndexer.ComputeIndexStrings(selectedRows.Count, mainIndex);
            var selectedLevels = new List<SelectedLevel>();
            var plannedViews = new List<PlannedView>();
            for (int i = 0; i < selectedRows.Count; i++)
            {
                var row = selectedRows[i];
                string idx = indices[i];
                selectedLevels.Add(new SelectedLevel
                {
                    SourceLevelId = row.SourceLevelId,
                    Name = row.Name,
                    Elevation = row.Elevation,
                    Index = idx
                });

                plannedViews.Add(new PlannedView
                {
                    ViewName = $"{idx} - {SetupConstants.FloorViewSuffix}",
                    Kind = ViewKind.Floor,
                    SourceLevelId = row.SourceLevelId,
                    LevelName = row.Name
                });
                plannedViews.Add(new PlannedView
                {
                    ViewName = $"{idx} - {SetupConstants.RcpViewSuffix}",
                    Kind = ViewKind.Rcp,
                    SourceLevelId = row.SourceLevelId,
                    LevelName = row.Name
                });
            }

            // ── Stage 2: view mapping (only when the running Revit can apply link overrides) ──
            // The firm hybrid needs LinkVisibility.Custom, which only the 2025+ API can write.
            // On 2024 we skip the mapping dialog entirely and set up levels/views/templates only.
            bool linkSupported = LinkStepSupported();
            var mapping = new Dictionary<string, ElementId>();

            if (linkSupported)
            {
                var linkedFloorViews = CollectLinkedViews(linkDoc, ViewType.FloorPlan);
                var linkedCeilingViews = CollectLinkedViews(linkDoc, ViewType.CeilingPlan);

                var stage2Rows = plannedViews.Select(pv =>
                {
                    var candidates = pv.Kind == ViewKind.Floor ? linkedFloorViews : linkedCeilingViews;
                    var row = new TurboSetupViewMappingViewModel.ViewMappingRow
                    {
                        ViewName = pv.ViewName,
                        SourceLevelId = pv.SourceLevelId
                    };
                    var none = new TurboSetupViewMappingViewModel.LinkedViewOption(ElementId.InvalidElementId, "(none)");
                    row.AvailableLinkedViews.Add(none);
                    foreach (var v in candidates)
                        row.AvailableLinkedViews.Add(new TurboSetupViewMappingViewModel.LinkedViewOption(v.Id, v.Name));

                    // No auto-preselect: every row defaults to "(none)". Name similarity between a
                    // linked view and a level is a coincidence, not a signal of which view the
                    // lighting set should base from — a confident-looking default would just get
                    // rubber-stamped. The designer must consciously choose the source view per row.
                    row.SelectedLinkedView = none;
                    return row;
                }).ToList();

                var stage2Vm = new TurboSetupViewMappingViewModel(stage2Rows);
                var stage2 = new TurboSetupViewMappingWindow(stage2Vm);
                new WindowInteropHelper(stage2) { Owner = owner };
                stage2.ShowDialog();

                if (!stage2Vm.Confirmed)
                    return Result.Cancelled;

                mapping = stage2Vm.GetMapping();
            }

            // ── Execution ──
            var result = Execute(uidoc, doc, linkDoc, linkInstance, linkTransform,
                selectedLevels, plannedViews, mapping, linkSupported);

            string summary =
                "TurboSetup Complete\n\n" +
                $"Levels copied: {result.LevelsCopied}\n" +
                $"Views created: {result.ViewsCreated}\n" +
                $"Views skipped (name already existed): {result.ViewsSkippedExisting}\n";
            if (result.LinkStepUnavailable)
            {
                summary += "Link graphics: skipped — requires Revit 2025 " +
                           "(set up the linked-view display manually in Revit 2024).";
            }
            else
            {
                summary +=
                    $"Link mappings applied: {result.LinkMappingsApplied}\n" +
                    $"Views with no linked view chosen: {result.ViewsUnmapped}\n" +
                    $"Link overrides rejected by Revit: {result.LinkApplyFailures}";
                if (!string.IsNullOrEmpty(result.LinkErrorSample))
                    summary += $"\n  → first error: {result.LinkErrorSample}";
            }
            if (result.Notes.Count > 0)
                summary += "\n\n" + string.Join("\n", result.Notes);
            TaskDialog.Show("TurboSetup", summary);

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("TurboSetup Error", $"An unexpected error occurred:\n{ex.Message}");
            return Result.Failed;
        }
    }

    /// <summary>
    /// Seed Space names from the architect Rooms. Blank-only (force=false) keeps manual disambiguation
    /// (LOWER POWDER / MAIN POWDER); a force pass re-pulls all. The blank-only/force choice is now made
    /// on the landing window's second page. Writes commit inside the service, so this returns Succeeded.
    /// </summary>
    private static Result RunNameSpaces(Document doc, bool force)
    {
        SpaceNamingResult r = SpaceNamingService.NameSpacesFromRooms(doc, force);

        string summary =
            $"Spaces examined: {r.Total}\r\n" +
            $"Named: {r.Named}\r\n" +
            $"Skipped (already named): {r.SkippedNamed}\r\n" +
            $"No architect Room (left as-is): {r.NoArchitectRoom}" +
            (r.NotWritable > 0 ? $"\r\nName not writable: {r.NotWritable}" : "") +
            (r.Preview.Count > 0 ? "\r\n\r\n" + string.Join("\r\n", r.Preview) : "");

        TaskDialog.Show("Name Spaces from Architect Rooms", summary);
        return Result.Succeeded;
    }

    /// <summary>True when the running Revit can write Custom link overrides (2025 and later).</summary>
    private static bool LinkStepSupported() =>
        int.TryParse(UpdateConstants.RevitVersion, out int year) && year >= 2025;

    private static SetupResult Execute(
        UIDocument uidoc,
        Document doc,
        Document linkDoc,
        RevitLinkInstance linkInstance,
        Transform linkTransform,
        IList<SelectedLevel> selectedLevels,
        IList<PlannedView> plannedViews,
        IDictionary<string, ElementId> mapping,
        bool linkSupported)
    {
        var result = new SetupResult();

        // Remember where Revit was. If anything fails, the group rolls back and restores the
        // original elements — but the active-view pointer would dangle at a rolled-back view,
        // leaving Revit view-less. We restore it explicitly on failure.
        ElementId originalActiveViewId = uidoc.ActiveView?.Id;

        using (var group = new TransactionGroup(doc, "TurboSetup"))
        {
            group.Start();
            try
            {
                // Snapshot every host level BEFORE copying — this exact set is the deletion target.
                var originalLevelIds = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Select(l => l.Id)
                    .ToList();

                var sourceIds = selectedLevels.Select(s => s.SourceLevelId).ToList();

                // 1. Copy the arch levels into the host (originals renamed out of the way, not yet deleted).
                Dictionary<ElementId, Level> sourceToHostLevel;
                using (var t = new Transaction(doc, "Copy levels"))
                {
                    t.Start();
                    sourceToHostLevel = LevelCopyService.CopyLevels(
                        doc, linkDoc, linkTransform, sourceIds, originalLevelIds);
                    t.Commit();
                }
                result.LevelsCopied = sourceToHostLevel.Count;

                // 1b. Turn the host Toposolid category off on every firm lighting template (AL_ prefix)
                //     BEFORE creating views, so each generated view inherits "Toposolid off" when
                //     ApplyViewTemplateParameters runs. The 2022-origin templates can't express this;
                //     the running 2024+ job can. This also reaches templates TurboSetup never creates
                //     views for — notably AL_Section, auto-applied to later section views — so the
                //     linked Toposolid stays suppressed across the whole lighting set.
                using (var t = new Transaction(doc, "Hide Toposolid on templates"))
                {
                    t.Start();
                    ToposolidVisibilityService.HideOnTemplates(doc);
                    t.Commit();
                }

                // 2. Create views and bake in the firm templates (one-shot, so the views stay free
                //    to take link overrides in step 5).
                List<ViewGenerationService.CreatedView> createdViews;
                using (var t = new Transaction(doc, "Create views"))
                {
                    t.Start();
                    createdViews = ViewGenerationService.CreateViews(
                        doc, plannedViews, sourceToHostLevel, out int skipped);
                    result.ViewsSkippedExisting = skipped;
                    t.Commit();
                }
                result.ViewsCreated = createdViews.Count;

                // 3. Move the active view onto a newly created view BEFORE deleting the originals.
                //    A fresh project opens on a plan view hosted by an original level; deleting that
                //    level cascades to its (active) view, which Revit refuses — aborting the delete.
                //    Switching off it first lets the cascade delete proceed. (No transaction open here.)
                if (createdViews.Count > 0)
                {
                    try { uidoc.ActiveView = createdViews[0].View; }
                    catch { /* best-effort; per-level fallback in DeleteOriginalLevels covers the rest */ }
                }

                // 4. Delete the host template's original levels.
                using (var t = new Transaction(doc, "Delete original levels"))
                {
                    t.Start();
                    int deleted = LevelCopyService.DeleteOriginalLevels(doc, originalLevelIds);
                    if (deleted < originalLevelIds.Count)
                        result.Notes.Add(
                            $"{originalLevelIds.Count - deleted} original level(s) could not be deleted and were left in place.");
                    t.Commit();
                }

                // 5. Apply the firm link-graphics hybrid (Revit 2025+ only). The views are
                //    template-free (one-shot ApplyViewTemplateParameters), so overrides stick.
                if (!linkSupported)
                {
                    // Revit 2024: Custom overrides aren't writable via the API — skip, set up manually.
                    result.LinkStepUnavailable = true;
                }
                else
                {
                    using (var t = new Transaction(doc, "Configure link graphics and view ranges"))
                    {
                        t.Start();
                        foreach (var cv in createdViews)
                        {
                            if (mapping.TryGetValue(cv.Planned.ViewName, out var linkedViewId)
                                && linkedViewId != ElementId.InvalidElementId)
                            {
                                try
                                {
                                    // Copy the architect's view range onto the host view first, so the
                                    // link (ViewRange = ByHostView) follows it.
                                    if (linkDoc.GetElement(linkedViewId) is ViewPlan linkedView)
                                        ViewRangeService.CopyFromLinkedView(cv.View, linkedView, linkDoc);

                                    // Target the link TYPE (not the instance) so the override lands on
                                    // the row the V/G dialog shows and replaces the template's default.
                                    var outcome = LinkGraphicsSeam.ApplyFirmHybrid(
                                        cv.View, linkInstance.GetTypeId(), linkedViewId);
                                    if (outcome == LinkGraphicsApplyResult.Applied)
                                        result.LinkMappingsApplied++;
                                    else
                                        result.LinkStepUnavailable = true;
                                }
                                catch (Exception ex)
                                {
                                    // A view/link that won't accept (or won't persist) the override:
                                    // leave it on host default V/G rather than abort the run, but
                                    // surface the real cause instead of hiding it.
                                    result.LinkApplyFailures++;
                                    result.LinkErrorSample ??= ex.Message;
                                }
                            }
                            else
                            {
                                result.ViewsUnmapped++;
                            }
                        }
                        t.Commit();
                    }
                }

                group.Assimilate();
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started)
                    group.RollBack();

                // The rollback restored the original views; point Revit back at one so it isn't view-less.
                if (originalActiveViewId != null && doc.GetElement(originalActiveViewId) is View originalView)
                {
                    try { uidoc.ActiveView = originalView; }
                    catch { /* nothing more we can safely do */ }
                }
                throw;
            }
        }

        return result;
    }

    private static List<View> CollectLinkedViews(Document linkDoc, ViewType viewType)
    {
        return new FilteredElementCollector(linkDoc)
            .OfClass(typeof(ViewPlan))
            .Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType == viewType)
            .OrderBy(v => v.Name)
            .ToList();
    }

}
