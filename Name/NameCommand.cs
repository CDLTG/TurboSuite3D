#nullable disable
using System;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Threading;
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

                var settings = CadRoomSourceSettingsCache.Get(doc);
                if (string.IsNullOrEmpty(settings.BlockName) && string.IsNullOrEmpty(settings.RoomNameLayer))
                {
                    TaskDialog.Show("TurboName",
                        "CAD Room Source is not configured.\n\n" +
                        "Open TurboSuite Settings and configure the CAD Room Source (Block or Text mode) before running TurboName.");
                    return Result.Cancelled;
                }

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

                var regions = RegionCollectorService.CollectRegions(doc, view);

                var cadRoomData = CadRoomExtractorService.ExtractRoomData(doc, view, settings);

                var (wallSegments, doorPositions, windowPositions) =
                    CadWallExtractorService.ExtractWallGeometry(doc, view, settings);

                var vm = new TurboNameViewModel
                {
                    RegionCount = regions.Count,
                    CadEntryCount = cadRoomData.Count,
                    WallSegmentCount = wallSegments.Count
                };

                var window = new TurboNameWindow { DataContext = vm };
                vm.CloseRequested += () =>
                {
                    window.DialogResult = true;
                    window.Close();
                };
                window.ShowDialog();

                if (vm.ShouldGenerate)
                    return LaunchGenerateRegions(commandData, doc, uidoc, view, settings,
                        wallSegments, doorPositions, windowPositions);

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

                Models.NamingResult result;
                using (var t = new Transaction(doc, "TurboName - Assign Room Names"))
                {
                    t.Start();
                    result = RegionNamingService.AssignRoomNames(
                        doc, view, regions, cadRoomData, textNoteType.Id, descTypeId);
                    t.Commit();
                }

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

        private static Result LaunchGenerateRegions(ExternalCommandData commandData,
            Document doc, UIDocument uidoc, View view, Shared.Models.CadRoomSourceSettings settings,
            System.Collections.Generic.List<Models.CadWallSegment> wallSegments,
            System.Collections.Generic.List<XYZ> doorPositions,
            System.Collections.Generic.List<XYZ> windowPositions)
        {
            if (wallSegments.Count == 0)
            {
                TaskDialog.Show("TurboName",
                    "No wall segments found in linked CAD files.\n\n" +
                    "Configure Wall Layer names in TurboSuite Settings.");
                return Result.Cancelled;
            }

            // Find the FilledRegionType
            string regionTypeName = string.IsNullOrEmpty(settings.RegionTypeName)
                ? "Room Region" : settings.RegionTypeName;
            var regionType = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .FirstOrDefault(t => t.Name == regionTypeName);

            if (regionType == null)
            {
                TaskDialog.Show("TurboName",
                    $"FilledRegionType \"{regionTypeName}\" not found in project.\n\n" +
                    "Create this type or update the Region Type Name in Settings.");
                return Result.Cancelled;
            }

            // Bridge gaps
            var bridgedSegments = GapBridgingService.BridgeGaps(wallSegments, doorPositions, windowPositions);

            // Create handler and external event
            var handler = new RegionGenerationHandler(doc, uidoc, view, bridgedSegments, regionType.Id);
            var externalEvent = ExternalEvent.Create(handler);

            var genVm = new GenerateRegionsViewModel(externalEvent, handler);
            var genWindow = new GenerateRegionsWindow { DataContext = genVm };

            var revitHandle = commandData.Application.MainWindowHandle;
            new WindowInteropHelper(genWindow) { Owner = revitHandle };

            genWindow.Closed += (s, e) =>
            {
                externalEvent.Dispose();
            };

            genVm.CloseRequested += () =>
            {
                genWindow.Close();
            };

            genWindow.Show();

            // Auto-start picking after the window renders
            genWindow.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                genVm.StartPicking();
            }));

            return Result.Succeeded;
        }
    }
}
