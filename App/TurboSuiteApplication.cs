using System;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace TurboSuite.App;

/// <summary>
/// External Application that registers the TurboSuite ribbon panels (Commands and Utilities).
/// </summary>
public class TurboSuiteApplication : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab("TurboSuite");
            RibbonPanel settingsPanel = application.CreateRibbonPanel("TurboSuite", "Settings");
            RibbonPanel commandsPanel = application.CreateRibbonPanel("TurboSuite", "Commands");
            RibbonPanel utilitiesPanel = application.CreateRibbonPanel("TurboSuite", "Utilities");
            RibbonPanel debugPanel = application.CreateRibbonPanel("TurboSuite", "Debug");
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Settings
            CreateButtonNoIcon(settingsPanel, assemblyPath,
                "TurboSettings",
                "Settings",
                "TurboSuite.App.SettingsCommand",
                "Configure TurboSuite settings",
                "Opens a dialog to configure which family names are treated as wall sconces, receptacles, and vertical electrical fixtures.");

            // Commands: Compact, Tag, Wire, Bubble
            CreateButton(commandsPanel, assemblyPath,
                "TurboCompact",
                "TurboCompact",
                "TurboSuite.Compact.CompactCommand",
                "Suggested shortcut: Ctrl+Shft+S\nClean and compact the active family",
                "Removes unused materials from the active family document and saves with the compact option to reduce file size.");

            CreateButton(commandsPanel, assemblyPath,
                "TurboTag",
                "TurboTag",
                "TurboSuite.Tag.TagCommand",
                "Suggested shortcut: TT\nAuto-place lighting fixture type tags",
                "Places type tags on selected lighting fixtures with configurable direction. Supports point-based, line-based, and face-based fixtures.");

            CreateButton(commandsPanel, assemblyPath,
                "TurboWire",
                "TurboWire",
                "TurboSuite.Wire.WireCommand",
                "Suggested shortcut: WW\nCreate wire connections between fixtures",
                "Creates arc wires between lighting fixtures. Supports pre-selected circuits, multiple fixtures by proximity, and wall sconce spline routing.");

            CreateButton(commandsPanel, assemblyPath,
                "TurboBubble",
                "TurboBubble",
                "TurboSuite.Bubble.BubbleCommand",
                "Suggested shortcut: TB\nCreate switchleg tag and wire for a lighting fixture",
                "Creates a switchleg tag and wire connection for the selected lighting fixture tag. Works in floor plan and ceiling plan views.");

            // Utilities: Name, Zones, Number, Driver
            CreateButtonNoIcon(utilitiesPanel, assemblyPath,
                "TurboName",
                "TurboName",
                "TurboSuite.Name.NameCommand",
                "Assign CAD room names to filled regions",
                "Opens a window to assign room names from linked DWG files to Room Region filled regions and place TextNotes. Also provides region generation (under construction).");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboZones",
                "TurboZones",
                "TurboSuite.Zones.ZonesCommand",
                "Update load names based on rooms and comments.",
                "Updates the Load Name parameter for every Electrical Circuit using the room location of the first lighting fixture and the circuit Comments or Load Classification.");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboNumber",
                "TurboNumber",
                "TurboSuite.Number.NumberCommand",
                "Update numbering for switchlegs, keypads, and power supplies.",
                "Opens a window to view and renumber electrical circuit numbers, device marks, and switch IDs for Keypad and Power Supply lighting devices.");

            CreateButtonNoIcon(utilitiesPanel, assemblyPath,
                "TurboRPS",
                "TurboRPS",
                "TurboSuite.Driver.RPSCommand",
                "Review power supply assignments for RPS circuits",
                "Opens a window to view electrical circuits with lighting devices and change device family types based on Switch ID groupings.");

            // TurboDriver: headless per-fixture power supply deployment
            CreateButtonNoIcon(commandsPanel, assemblyPath,
                "TurboDriver",
                "TurboDriver",
                "TurboSuite.Driver.DriverCommand",
                "Suggested shortcut: TD\nDeploy power supplies for selected fixtures",
                "Select lighting fixtures with Remote Power Supply, then deploy recommended power supplies. Creates an electrical circuit if one doesn't exist.");

            // TurboSpike: diagnostic/troubleshooting command
            CreateButtonNoIcon(debugPanel, assemblyPath,
                "TurboSpike",
                "TurboSpike",
                "TurboSuite.Spike.SpikeCommand",
                "Diagnostic command for troubleshooting",
                "Runs diagnostic probes against the Revit API. Swap out the Execute body as needed for each investigation.");

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
        button.LargeImage = new BitmapImage(
            new Uri($"pack://application:,,,/TurboSuite;component/Resources/Icons/{name}_32.png"));
        button.Image = new BitmapImage(
            new Uri($"pack://application:,,,/TurboSuite;component/Resources/Icons/{name}_16.png"));
    }

    private static void CreateButtonNoIcon(RibbonPanel panel, string assemblyPath,
        string name, string text, string className, string tooltip, string longDescription)
    {
        PushButtonData buttonData = new PushButtonData(name, text, assemblyPath, className);
        PushButton button = (PushButton)panel.AddItem(buttonData);
        button.ToolTip = tooltip;
        button.LongDescription = longDescription;
    }
}
