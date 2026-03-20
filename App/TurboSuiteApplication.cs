using System;
using System.Reflection;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using TurboSuite.Tab;

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
                "  Settings   ",
                "TurboSuite.App.SettingsCommand",
                "Configure TurboSuite settings",
                "Opens a dialog to configure which family names are treated as wall sconces, receptacles, and vertical electrical fixtures.");

            // TurboTab: document tab coloring toggle
            CreateButtonNoIcon(settingsPanel, assemblyPath,
                "TurboTab",
                "    Turbo    \n     Tab     ",
                "TurboSuite.Tab.TabCommand",
                "Toggle document tab coloring",
                "Colors each open document tab with a distinct background color for easy visual identification. State persists across sessions.");

            // Auto-start tab coloring after Revit UI is fully loaded.
            if (TabSettingsService.LoadEnabled())
            {
                application.Idling += OnIdlingStartTabColoring;
            }

            // Commands: Compact, Tag, Wire, Bubble
            CreateButtonNoIcon(commandsPanel, assemblyPath,
                "TurboCompact",
                "    Turbo    \n   Compact   ",
                "TurboSuite.Compact.CompactCommand",
                "Suggested shortcut: Ctrl+Shft+S\nClean and compact the active family",
                "Removes unused materials from the active family document and saves with the compact option to reduce file size.");

            CreateButtonNoIcon(commandsPanel, assemblyPath,
                "TurboTag",
                "    Turbo    \n     Tag     ",
                "TurboSuite.Tag.TagCommand",
                "Suggested shortcut: TT\nAuto-place lighting fixture type tags",
                "Places type tags on selected lighting fixtures with configurable direction. Supports point-based, line-based, and face-based fixtures.");

            CreateButtonNoIcon(commandsPanel, assemblyPath,
                "TurboWire",
                "    Turbo    \n    Wire     ",
                "TurboSuite.Wire.WireCommand",
                "Suggested shortcut: WW\nCreate wire connections between fixtures",
                "Creates arc wires between lighting fixtures. Supports pre-selected circuits, multiple fixtures by proximity, and wall sconce spline routing.");

            CreateButtonNoIcon(commandsPanel, assemblyPath,
                "TurboBubble",
                "    Turbo    \n   Bubble    ",
                "TurboSuite.Bubble.BubbleCommand",
                "Suggested shortcut: TB\nCreate switchleg tag and wire for a lighting fixture",
                "Creates a switchleg tag and wire connection for the selected lighting fixture tag. Works in floor plan and ceiling plan views.");

            // Utilities: Name, Zones, Number, Driver
            CreateButtonNoIcon(utilitiesPanel, assemblyPath,
                "TurboName",
                "    Turbo    \n    Name     ",
                "TurboSuite.Name.NameCommand",
                "Assign CAD room names to filled regions",
                "Opens a window to assign room names from linked DWG files to Room Region filled regions and place TextNotes. Also provides region generation (under construction).");

            CreateButtonNoIcon(utilitiesPanel, assemblyPath,
                "TurboZones",
                "    Turbo    \n    Zones    ",
                "TurboSuite.Zones.ZonesCommand",
                "Update load names based on rooms and comments.",
                "Updates the Load Name parameter for every Electrical Circuit using the room location of the first lighting fixture and the circuit Comments or Load Classification.");

            CreateButtonNoIcon(utilitiesPanel, assemblyPath,
                "TurboNumber",
                "    Turbo    \n   Number    ",
                "TurboSuite.Number.NumberCommand",
                "Update numbering for switchlegs, keypads, and power supplies.",
                "Opens a window to view and renumber electrical circuit numbers, device marks, and switch IDs for Keypad and Power Supply lighting devices.");

            CreateButtonNoIcon(utilitiesPanel, assemblyPath,
                "TurboRPS",
                "    Turbo    \n     RPS     ",
                "TurboSuite.Driver.RPSCommand",
                "Review power supply assignments for RPS circuits",
                "Opens a window to view electrical circuits with lighting devices and change device family types based on Switch ID groupings.");

            // TurboDriver: headless per-fixture power supply deployment
            CreateButtonNoIcon(commandsPanel, assemblyPath,
                "TurboDriver",
                "    Turbo    \n   Driver    ",
                "TurboSuite.Driver.DriverCommand",
                "Suggested shortcut: TD\nDeploy power supplies for selected fixtures",
                "Select lighting fixtures with Remote Power Supply, then deploy recommended power supplies. Creates an electrical circuit if one doesn't exist.");

            // TurboSpike: diagnostic/troubleshooting command
            CreateButtonNoIcon(debugPanel, assemblyPath,
                "TurboSpike",
                "    Turbo    \n    Spike    ",
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
        TabColoringService.Stop();
        return Result.Succeeded;
    }

    private static int _tabStartRetries;

    private static void OnIdlingStartTabColoring(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication uiApp) return;

        _tabStartRetries++;
        bool started = TabColoringService.Start(uiApp.MainWindowHandle, uiApp);

        if (started || _tabStartRetries > 50)
            uiApp.Idling -= OnIdlingStartTabColoring;
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
