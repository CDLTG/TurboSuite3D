using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Tab;

/// <summary>
/// TurboTab — toggles document tab coloring by walking Revit's AvalonDock visual tree.
/// Caches original <c>TabItem.Style</c> before modification and restores on toggle-off
/// (see CLAUDE.md WPF Patterns — never use <c>ClearValue(StyleProperty)</c>).
/// </summary>
[Transaction(TransactionMode.Manual)]
public class TabCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            if (TabColoringService.IsRunning)
            {
                TabColoringService.Stop();
                TabSettingsService.SaveEnabled(false);
            }
            else
            {
                TabColoringService.Start(commandData.Application.MainWindowHandle, commandData.Application);
                TabSettingsService.SaveEnabled(true);
            }

            return Result.Succeeded;
        }
        catch (System.Exception ex)
        {
            TaskDialog.Show("TurboTab Error", $"An unexpected error occurred:\n{ex.Message}");
            return Result.Failed;
        }
    }
}
