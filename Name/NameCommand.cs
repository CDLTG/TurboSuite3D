#nullable disable
using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Name.ViewModels;
using TurboSuite.Name.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Name
{
    /// <summary>
    /// TurboName — Opens a window for CAD-based room name assignment and region generation.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class NameCommand : IExternalCommand
    {
        private const string TextNoteTypeName = "AL_Annotation_4.5\"";
        private const string DescriptionTextNoteTypeName = "AL_Annotation_3\"";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc?.Document;

                if (doc == null)
                {
                    TaskDialog.Show("TurboName", "No active document found.");
                    return Result.Failed;
                }

                View view = doc.ActiveView;

                // Load CAD Room Source settings
                var settings = CadRoomSourceSettingsCache.Get(doc);
                if (string.IsNullOrEmpty(settings.BlockName) && string.IsNullOrEmpty(settings.RoomNameLayer))
                {
                    TaskDialog.Show("TurboName",
                        "CAD Room Source is not configured.\n\n" +
                        "Open TurboSuite Settings and configure the CAD Room Source (Block or Text mode) before running TurboName.");
                    return Result.Cancelled;
                }

                // Look up TextNoteType
                var textNoteType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault(t => t.Name == TextNoteTypeName);

                if (textNoteType == null)
                {
                    TaskDialog.Show("TurboName",
                        $"TextNote type \"{TextNoteTypeName}\" not found in this document.\n\n" +
                        "Load the annotation type into the project before running TurboName.");
                    return Result.Cancelled;
                }

                // Look up description TextNoteType (non-fatal if missing)
                var descTextNoteType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault(t => t.Name == DescriptionTextNoteTypeName);
                ElementId descTypeId = descTextNoteType?.Id ?? ElementId.InvalidElementId;

                // Collect Room Region filled regions
                var regions = RegionCollectorService.CollectRegions(doc, view);

                // Extract CAD room data
                var cadRoomData = CadRoomExtractorService.ExtractRoomData(doc, view, settings);

                // Show the TurboName window
                var vm = new TurboNameViewModel
                {
                    RegionCount = regions.Count,
                    CadEntryCount = cadRoomData.Count
                };

                var window = new TurboNameWindow { DataContext = vm };
                vm.CloseRequested += () =>
                {
                    window.DialogResult = true;
                    window.Close();
                };
                window.ShowDialog();

                if (!vm.ShouldRun)
                    return Result.Cancelled;

                if (regions.Count == 0)
                {
                    TaskDialog.Show("TurboName",
                        "No \"Room Region\" filled regions found in the active view.\n\n" +
                        "Draw filled regions using the \"Room Region\" type, then run TurboName.");
                    return Result.Cancelled;
                }

                if (cadRoomData.Count == 0)
                {
                    TaskDialog.Show("TurboName",
                        "No room data found in linked CAD files.\n\n" +
                        "Verify the CAD Room Source settings match the linked DWG content.");
                    return Result.Cancelled;
                }

                // Assign room names inside a single transaction
                Models.NamingResult result;
                using (var t = new Transaction(doc, "TurboName - Assign Room Names"))
                {
                    t.Start();
                    result = RegionNamingService.AssignRoomNames(
                        doc, view, regions, cadRoomData, textNoteType.Id, descTypeId);
                    t.Commit();
                }

                // Summary
                var summary = $"TurboName Complete\n\n" +
                    $"Processed: {result.Processed}\n" +
                    $"Skipped (existing Comments): {result.Skipped}\n" +
                    $"Ambiguous (multiple names): {result.Ambiguous}\n" +
                    $"Unmatched (no CAD data): {result.Unmatched}";

                if (result.AmbiguousDetails.Count > 0)
                {
                    summary += "\n\nAmbiguous regions — conflicting names found:";
                    foreach (var ar in result.AmbiguousDetails)
                        summary += $"\n  - {string.Join(" vs. ", ar.Names)}";
                }

                TaskDialog.Show("TurboName", summary);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboName Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
