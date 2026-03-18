using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Tab;

[Transaction(TransactionMode.Manual)]
public class TabCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
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
}
