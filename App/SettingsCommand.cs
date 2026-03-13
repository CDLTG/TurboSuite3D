using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.App.ViewModels;
using TurboSuite.App.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.App;

[Transaction(TransactionMode.Manual)]
public class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show("TurboSuite Settings", "No active document found.");
                return Result.Failed;
            }

            var familySettings = FamilyNameSettingsCache.Get(doc);
            var cadSettings = CadRoomSourceSettingsCache.Get(doc);
            var viewModel = new SettingsViewModel(familySettings, cadSettings);
            var window = new SettingsWindow { DataContext = viewModel };
            var helper = new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };

            if (window.ShowDialog() == true)
            {
                FamilyNameSettingsStorageService.Save(doc, viewModel.ToFamilyModel());
                FamilyNameSettingsCache.Invalidate();

                CadRoomSourceStorageService.Save(doc, viewModel.ToCadModel());
                CadRoomSourceSettingsCache.Invalidate();
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
