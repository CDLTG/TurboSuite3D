using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace TurboSuite.App;

/// <summary>
/// External Application that registers the TurboSuite ribbon panel with all four command buttons.
/// </summary>
public class TurboSuiteApplication : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab("TurboSuite");
            RibbonPanel panel = application.CreateRibbonPanel("TurboSuite", "Commands");
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            CreateButton(panel, assemblyPath,
                "TurboDriver",
                "TurboDriver",
                "TurboSuite.Driver.DriverCommand",
                "Manage Lighting Device family types based on circuit information",
                "Opens a window to view electrical circuits with lighting devices and change device family types based on Switch ID groupings.");

            CreateButton(panel, assemblyPath,
                "TurboBubble",
                "TurboBubble",
                "TurboSuite.Bubble.BubbleCommand",
                "Create switchleg tag and wire for a lighting fixture",
                "Creates a switchleg tag and wire connection for the selected lighting fixture tag. Works in floor plan and ceiling plan views.");

            CreateButton(panel, assemblyPath,
                "TurboTag",
                "TurboTag",
                "TurboSuite.Tag.TagCommand",
                "Auto-place lighting fixture type tags",
                "Places type tags on selected lighting fixtures with configurable direction. Supports point-based, line-based, and face-based fixtures.");

            CreateButton(panel, assemblyPath,
                "TurboWire",
                "TurboWire",
                "TurboSuite.Wire.WireCommand",
                "Create wire connections between fixtures",
                "Creates arc wires between lighting fixtures. Supports pre-selected circuits, multiple fixtures by proximity, and wall sconce spline routing.");

            CreateButton(panel, assemblyPath,
                "TurboZones",
                "TurboZones",
                "TurboSuite.Zones.ZonesCommand",
                "Update load names based on rooms and comments.",
                "Updates the Load Name parameter for every Electrical Circuit using the room location of the first lighting fixture and the circuit Comments or Load Classification.");

            CreateButton(panel, assemblyPath,
                "TurboNumber",
                "TurboNumber",
                "TurboSuite.Number.NumberCommand",
                "Update numbering for switchlegs, keypads, and power supplies.",
                "Opens a window to view and renumber electrical circuit numbers, device marks, and switch IDs for Keypad and Power Supply lighting devices.");

            CreateButton(panel, assemblyPath,
                "TurboCompact",
                "TurboCompact",
                "TurboSuite.Compact.CompactCommand",
                "Clean and compact the active family",
                "Removes unused materials from the active family document and saves with the compact option to reduce file size.");

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("TurboSuite Error", $"Failed to initialize TurboSuite:\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }

    private static void CreateButton(RibbonPanel panel, string assemblyPath,
        string name, string text, string className, string tooltip, string longDescription)
    {
        PushButtonData buttonData = new PushButtonData(name, text, assemblyPath, className);
        PushButton button = (PushButton)panel.AddItem(buttonData);
        button.ToolTip = tooltip;
        button.LongDescription = longDescription;
    }
}
