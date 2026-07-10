#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Windows.Interop;
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
    // NOTE: no [Regeneration(RegenerationOption.Manual)] — that obsolete attribute suppresses automatic
    // document regeneration, which left DataStorage.Create+SetEntity unfinalized so Revit purged the
    // (apparently empty) DataStorage on the next regen. That was the real "CAD settings don't persist"
    // root cause. Absent = automatic regeneration, matching SettingsCommand (whose settings persist).
    [Transaction(TransactionMode.Manual)]
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

                var cadSettings = CadRoomSourceSettingsCache.Get(doc);
                var vm = new TurboNameViewModel(cadSettings, uidoc);

                // Round-trip loop. Both "Pick from view" and "Save" must run OUTSIDE the dialog's own modal
                // loop — PickObject can't run under it, and a Revit transaction only persists reliably once
                // the modal dialog has closed. So the window closes, we act in clean context, then reopen it
                // bound to the SAME VM at the SAME on-screen position.
                // "Pick from view" is the ONLY thing that needs the close→act→reopen round-trip
                // (PickObject can't run under the dialog's own modal loop). Settings are dirty-tracked in the
                // VM and auto-saved once, after the window finally closes — never inside the loop.
                double? left = null, top = null;
                while (true)
                {
                    var window = new TurboNameWindow { DataContext = vm };
                    if (left.HasValue)
                    {
                        window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                        window.Left = left.Value;
                        window.Top = top.Value;
                    }
                    new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
                    window.ShowDialog();
                    left = window.Left;
                    top = window.Top;

                    if (vm.CadConfig.PickRequested)
                    {
                        vm.CadConfig.PickRequested = false;
                        vm.CadConfig.RunPick();
                        continue;
                    }
                    break;
                }

                // Auto-save on close: persist once the window is closed (nothing modal follows the commit),
                // only if something actually changed or we're about to act on the config.
                bool persisted = vm.SettingsDirty || vm.ShouldRun || vm.ShouldGenerate;
                if (persisted)
                    PersistCadConfig(doc, vm.CadConfig);

                if (!vm.ShouldRun && !vm.ShouldGenerate)
                    // Return Succeeded (not Cancelled) when we persisted — Revit DISCARDS a command's
                    // committed changes if it returns Cancelled/Failed, which silently rolls back the
                    // just-saved settings DataStorage.
                    return persisted ? Result.Succeeded : Result.Cancelled;

                var settings = CadRoomSourceSettingsCache.Get(doc);

                if (vm.ShouldGenerate)
                    return LaunchGenerateRegions(commandData, doc, uidoc, view, settings);

                // ── Assign Room Names path ──
                if (string.IsNullOrEmpty(settings.BlockName) && string.IsNullOrEmpty(settings.RoomNameLayer))
                {
                    TaskDialog.Show("TurboName",
                        "CAD Room Source is not configured.\n\n" +
                        "Configure the CAD Room Source (Block or Text mode) in the TurboName window before running.");
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

                // Collect data only when Run is clicked
                var regions = RegionCollectorService.CollectRegions(doc, view);
                if (regions.Count == 0)
                {
                    TaskDialog.Show("TurboName",
                        "No \"Room Region\" filled regions found in the active view.\n\n" +
                        "Draw filled regions using the \"Room Region\" type, then run TurboName.");
                    return Result.Cancelled;
                }

                var cadRoomData = CadRoomExtractorService.ExtractRoomData(doc, view, settings);
                if (cadRoomData.Count == 0)
                {
                    TaskDialog.Show("TurboName",
                        "No room data found in linked CAD files.\n\n" +
                        "Verify the CAD Room Source settings match the linked DWG content.");
                    return Result.Cancelled;
                }

                // Look up description TextNoteType (non-fatal if missing)
                var descTextNoteType = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault(t => t.Name == DescriptionTextNoteTypeName);
                ElementId descTypeId = descTextNoteType?.Id ?? ElementId.InvalidElementId;

                // Look up Room Region type for unflagging
                var roomRegionType = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .FirstOrDefault(rt => rt.Name == "Room Region");
                ElementId roomRegionTypeId = roomRegionType?.Id;

                Models.NamingResult result;
                using (var t = new Transaction(doc, "TurboName - Assign Room Names"))
                {
                    t.Start();
                    result = RegionNamingService.AssignRoomNames(
                        doc, view, regions, cadRoomData, textNoteType.Id, descTypeId, roomRegionTypeId);

                    // Flag ambiguous regions so they're easy to find
                    if (result.AmbiguousDetails.Count > 0)
                    {
                        var flaggedType = new FilteredElementCollector(doc)
                            .OfClass(typeof(FilledRegionType))
                            .Cast<FilledRegionType>()
                            .FirstOrDefault(rt => rt.Name == "Room Region (Flagged)");

                        if (flaggedType != null)
                        {
                            foreach (var ar in result.AmbiguousDetails)
                                doc.GetElement(ar.RegionId)?.ChangeTypeId(flaggedType.Id);
                        }
                    }

                    // Flag unmatched regions so they're easy to find
                    if (result.UnmatchedRegionIds.Count > 0)
                    {
                        var emptyType = new FilteredElementCollector(doc)
                            .OfClass(typeof(FilledRegionType))
                            .Cast<FilledRegionType>()
                            .FirstOrDefault(rt => rt.Name == "Room Region (Empty)");

                        if (emptyType != null)
                        {
                            foreach (var id in result.UnmatchedRegionIds)
                                doc.GetElement(id)?.ChangeTypeId(emptyType.Id);
                        }
                    }

                    t.Commit();
                }

                var summary = $"TurboName Complete\n\n" +
                    $"Processed: {result.Processed}\n" +
                    $"Skipped (existing Comments): {result.Skipped}\n" +
                    $"Ambiguous (multiple names): {result.Ambiguous}\n" +
                    $"Unmatched (no CAD data): {result.Unmatched}";

                TaskDialog.Show("TurboName", summary);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (IOException ioEx)
            {
                TaskDialog.Show("TurboName", ioEx.Message);
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboName Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }

        private static void PersistCadConfig(Document doc, ViewModels.CadRoomSourceConfigViewModel config)
        {
            CadRoomSourceStorageService.Save(doc, config.ToModel());
            CadRoomSourceSettingsCache.Invalidate();
        }

        private static Result LaunchGenerateRegions(ExternalCommandData commandData,
            Document doc, UIDocument uidoc, View view, Shared.Models.CadRoomSourceSettings settings)
        {
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

            // Create handler and external event
            var handler = new RegionPickHandler(doc, uidoc, view, regionType.Id, settings);
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

            ModelessWindowGuard.Register(doc, genWindow, genWindow.Close);
            genWindow.Show();

            return Result.Succeeded;
        }
    }
}
