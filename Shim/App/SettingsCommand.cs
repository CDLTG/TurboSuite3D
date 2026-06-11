using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.App.ViewModels;
using TurboSuite.App.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.App;

/// <summary>
/// Opens the TurboSuite Settings dialog. Edits family-name/CAD-source/general settings stored
/// in project ExtensibleStorage. No model elements are modified directly.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
            {
                TaskDialog.Show("TurboSuite Settings", "No active document found.");
                return Result.Failed;
            }

            var familySettings = FamilyNameSettingsCache.Get(doc);
            var cadSettings = CadRoomSourceSettingsCache.Get(doc);
            var generalSettings = GeneralSettingsCache.Get(doc);
            var viewModel = new SettingsViewModel(familySettings, cadSettings, generalSettings, uidoc);

            // "Pick from view" can't run PickObject while the dialog's own modal ShowDialog loop is
            // active (nested modal corrupts the dialog). So the dialog closes with PickRequested set;
            // we run the pick here in clean context, then reopen a fresh window bound to the SAME
            // ViewModel — all in-progress edits are preserved on the VM across the round-trip.
            bool save = false;
            while (true)
            {
                var window = new SettingsWindow { DataContext = viewModel };
                new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };

                bool? result = window.ShowDialog();

                if (viewModel.PickRequested)
                {
                    viewModel.PickRequested = false;
                    viewModel.RunPick();
                    continue; // reopen with the picked values filled in
                }

                save = result == true;
                break;
            }

            if (save)
            {
                FamilyNameSettingsStorageService.Save(doc, viewModel.ToFamilyModel());
                FamilyNameSettingsCache.Invalidate();

                CadRoomSourceStorageService.Save(doc, viewModel.ToCadModel());
                CadRoomSourceSettingsCache.Invalidate();

                GeneralSettingsStorageService.Save(doc, viewModel.ToGeneralModel());
                GeneralSettingsCache.Invalidate();
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("TurboSuite Settings Error", $"An unexpected error occurred:\n{ex.Message}");
            return Result.Failed;
        }
    }
}
