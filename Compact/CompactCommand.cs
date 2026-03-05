using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Compact;

[Transaction(TransactionMode.Manual)]
public class CompactCommand : IExternalCommand
{
    public Result Execute(
        ExternalCommandData commandData,
        ref string message,
        ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        if (!doc.IsFamilyDocument)
        {
            TaskDialog.Show("TurboCompact",
                "TurboCompact must be run from within the Revit Family Editor.\n" +
                "Open a family file (.rfa) before running this command.");
            return Result.Cancelled;
        }

        var dialog = new TaskDialog("TurboCompact")
        {
            MainInstruction = "Clean and compact this family?",
            MainContent =
                "This will perform the following operations:\n" +
                "  1. Remove unused materials\n" +
                "  2. Save with compact option\n\n" +
                $"Family: {doc.Title}",
            CommonButtons = TaskDialogCommonButtons.Cancel
        };
        dialog.AddCommandLink(
            TaskDialogCommandLinkId.CommandLink1,
            "Proceed",
            "Remove unused materials and compact-save.");

        if (dialog.Show() != TaskDialogResult.CommandLink1)
            return Result.Cancelled;

        try
        {
            int deletedCount = DeleteUnusedMaterials(doc);

            doc.Save(new SaveOptions { Compact = true });

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static int DeleteUnusedMaterials(Document doc)
    {
        var allMaterialIds = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .ToElementIds()
            .ToHashSet();

        var usedIds = new HashSet<ElementId>();

        void CollectFrom(IEnumerable<Element> elems)
        {
            foreach (Element elem in elems)
            {
                try
                {
                    foreach (ElementId id in elem.GetMaterialIds(false)) usedIds.Add(id);
                    foreach (ElementId id in elem.GetMaterialIds(true))  usedIds.Add(id);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
        }

        CollectFrom(new FilteredElementCollector(doc).WhereElementIsNotElementType());
        CollectFrom(new FilteredElementCollector(doc).WhereElementIsElementType());

        // Subcategory appearance materials are not returned by GetMaterialIds
        foreach (Category cat in doc.Settings.Categories)
        {
            if (cat.Material != null) usedIds.Add(cat.Material.Id);
            foreach (Category sub in cat.SubCategories)
                if (sub.Material != null) usedIds.Add(sub.Material.Id);
        }

        var toDelete = allMaterialIds.Where(id => !usedIds.Contains(id)).ToList();
        if (toDelete.Count == 0) return 0;

        int deleted = 0;
        using (var trans = new Transaction(doc, "TurboCompact - Remove Unused Materials"))
        {
            trans.Start();
            foreach (ElementId id in toDelete)
            {
                try { doc.Delete(id); deleted++; }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            }
            trans.Commit();
        }

        return deleted;
    }
}
