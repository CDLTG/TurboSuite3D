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
/// Opens the TurboSuite Settings dialog. Edits general settings stored in project
/// ExtensibleStorage. No model elements are modified directly.
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

            var generalSettings = GeneralSettingsCache.Get(doc);
            var viewModel = new SettingsViewModel(generalSettings);

            var window = new SettingsWindow { DataContext = viewModel };
            new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
            bool? result = window.ShowDialog();

            if (result == true)
            {
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
