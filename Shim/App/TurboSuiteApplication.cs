using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using TurboSuite.Shared.Services;
using TurboSuite.Tab;

namespace TurboSuite.App;

/// <summary>
/// External Application that registers the TurboSuite ribbon panels — Settings, Commands, Utilities, and
/// (gated on <see cref="ExperimentalCommandsEnabled"/>) Controls and Debug, in that ribbon order.
/// </summary>
public class TurboSuiteApplication : IExternalApplication
{
    // Gates experimental commands (e.g., TurboDMX, TurboDALI) so they ship compiled but
    // unreachable until they're ready. `static readonly` (not `const`) so the compiler doesn't
    // flag the gated branch as unreachable (CS0162). TurboDALI owns DALI loop declaration outright now
    // (the transitional TurboZones DALI tab is gone) — DALI editing is dev-only until this gate ungates.
    public static readonly bool ExperimentalCommandsEnabled = true;

    private static bool _updateAccepted;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // The running Revit supplies the version year — the shared shim source is
            // identical across every per-version DLL, so all local-state and addins paths
            // (%LOCALAPPDATA%\TurboSuite\{ver}\, Addins\{ver}\) isolate off this at runtime.
            UpdateConstants.RevitVersion = application.ControlledApplication.VersionNumber;

            // Close any open modeless window when the document it was opened against closes — otherwise it
            // lingers holding a dead document and crashes Revit on the next interaction.
            ModelessWindowGuard.Hook(application.ControlledApplication);

            application.CreateRibbonTab("TurboSuite");
            RibbonPanel settingsPanel = application.CreateRibbonPanel("TurboSuite", "Settings");
            RibbonPanel commandsPanel = application.CreateRibbonPanel("TurboSuite", "Commands");
            RibbonPanel utilitiesPanel = application.CreateRibbonPanel("TurboSuite", "Utilities");
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // ── Settings panel ──
            CreateButton(settingsPanel, assemblyPath,
                "TurboSettings",
                "  Settings   ",
                "TurboSuite.App.SettingsCommand",
                "Configure TurboSuite settings",
                "Opens a dialog to configure which family names are treated as wall sconces, receptacles, and vertical electrical fixtures.",
                "TurboSettings");

            CreateButton(settingsPanel, assemblyPath,
                "TurboTab",
                "     Tab     ",
                "TurboSuite.Tab.TabCommand",
                "Toggle document tab coloring",
                "Colors each open document tab with a distinct background color for easy visual identification. State persists across sessions.",
                "TurboTab");

            // Auto-start tab coloring after Revit UI is fully loaded.
            if (TabSettingsService.LoadEnabled())
            {
                application.Idling += OnIdlingStartTabColoring;
            }

            // ── Commands panel ──
            CreateButton(commandsPanel, assemblyPath,
                "TurboCompact",
                "   Compact   ",
                "TurboSuite.Compact.CompactCommand",
                "Suggested shortcut: Ctrl+Shft+S\nClean and compact the active family",
                "Removes unused materials from the active family document and saves with the compact option to reduce file size.",
                "TurboCompact");

            CreateButton(commandsPanel, assemblyPath,
                "TurboSnoop",
                "    Snoop    ",
                "TurboSuite.Snoop.SnoopCommand",
                "Suggested shortcut: TS\nList the Visibility/Graphics checkboxes a linked family draws under",
                "Pick a linked architectural family to list the Visibility/Graphics Category → Subcategory checkboxes its geometry draws under (model geometry vs view-dependent annotation) — so you know which VG → RVT Links checkbox controls a clearance/path/egress line. Read-only.",
                "TurboSnoop");

            CreateButton(commandsPanel, assemblyPath,
                "TurboTag",
                "     Tag     ",
                "TurboSuite.Tag.TagCommand",
                "Suggested shortcut: TT\nAuto-place lighting fixture type tags",
                "Places type tags on selected lighting fixtures with configurable direction. Supports point-based, line-based, and face-based fixtures.",
                "TurboTag");

            CreateButton(commandsPanel, assemblyPath,
                "TurboWire",
                "    Wire     ",
                "TurboSuite.Wire.WireCommand",
                "Suggested shortcut: WW\nCreate wire connections between fixtures",
                "Creates arc wires between lighting fixtures. Supports pre-selected circuits, multiple fixtures by proximity, and wall sconce spline routing.",
                "TurboWire");

            CreateButton(commandsPanel, assemblyPath,
                "TurboBubble",
                "   Bubble    ",
                "TurboSuite.Bubble.BubbleCommand",
                "Suggested shortcut: TB\nCreate switchleg tag and wire for a lighting fixture",
                "Creates a switchleg tag and wire connection for the selected lighting fixture tag. Works in floor plan and ceiling plan views.",
                "TurboBubble");

            // ── Utilities panel ──
            CreateButton(utilitiesPanel, assemblyPath,
                "TurboSetup",
                "    Setup    ",
                "TurboSuite.Setup.SetupCommand",
                "Set up a new project from the linked architectural model",
                "Copies levels from the linked architectural model, creates Floor Plan and RCP views per level with firm view templates, and wires each view's link graphics to a chosen architectural view. 3D RVT-linked projects only.",
                "TurboSetup");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboName",
                "    Name     ",
                "TurboSuite.Name.NameCommand",
                "Assign CAD room names to filled regions",
                "Opens a window to assign room names from linked DWG files to Room Region filled regions and place TextNotes. Also provides region generation (under construction).",
                "TurboName");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboSchedule",
                "  Schedule   ",
                "TurboSuite.Schedule.ScheduleCommand",
                "Edit fixture and driver specs one type per page",
                "Opens a form-view editor for lighting fixture and driver type specifications — one Type Mark per page. Edits apply to every type instance and save in a single undo step.",
                "TurboSchedule");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboZones",
                "    Zones    ",
                "TurboSuite.Zones.ZonesCommand",
                "Update load names based on rooms and comments.",
                "Updates the Load Name parameter for every Electrical Circuit using the room location of the first lighting fixture and the circuit Comments or Load Classification.",
                "TurboZones");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboNumber",
                "   Number    ",
                "TurboSuite.Number.NumberCommand",
                "Update numbering for switchlegs, keypads, and power supplies.",
                "Opens a window to view and renumber electrical circuit numbers, device marks, and switch IDs for Keypad and Power Supply lighting devices.",
                "TurboNumber");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboRPS",
                "     RPS     ",
                "TurboSuite.Driver.RPSCommand",
                "Review power supply assignments for RPS circuits",
                "Opens a window to view electrical circuits with lighting devices and change device family types based on Switch ID groupings.",
                "TurboRPS");

            CreateButton(utilitiesPanel, assemblyPath,
                "TurboDocs",
                "    Docs     ",
                "TurboSuite.Docs.DocsCommand",
                "Generate fixture documentation PDFs",
                "Opens a tabbed utility for generating cut sheet and fixture schedule PDFs from lighting fixture types in the active document.",
                "TurboDocs");

            CreateButton(commandsPanel, assemblyPath,
                "TurboDriver",
                "   Driver    ",
                "TurboSuite.Driver.DriverCommand",
                "Suggested shortcut: TD\nDeploy power supplies for selected fixtures",
                "Select lighting fixtures with Remote Power Supply, then deploy recommended power supplies. Creates an electrical circuit if one doesn't exist.",
                "TurboDriver");

            CreateButton(commandsPanel, assemblyPath,
                "TurboMask",
                "    Mask     ",
                "TurboSuite.Mask.MaskCommand",
                "Suggested shortcut: BB\nMask selected elements while preserving fixture graphics",
                "Places a masking region around the selected elements and overlays a view-level annotation stamp at each lighting fixture so the visible footprint graphics remain readable on top of the mask.",
                "TurboMask");

            // ── Controls panel (TurboDMX + TurboDALI) ──
            // The digital-lighting-control commands, kept as separate buttons in their own panel. Rides the
            // shared ExperimentalCommandsEnabled gate (both commands are experimental), so the whole panel
            // ships compiled but hidden. Created here — after Utilities, before the Debug panel below — so it
            // lands between them on the ribbon (panels render in creation order).
            if (ExperimentalCommandsEnabled)
            {
                RibbonPanel controlsPanel = application.CreateRibbonPanel("TurboSuite", "Controls");

                CreateButton(controlsPanel, assemblyPath,
                    "TurboDMX",
                    "     DMX     ",
                    "TurboSuite.Dmx.DmxCommand",
                    "Automate DMX-controlled RGBW LED tape systems",
                    "Opens the TurboDMX window to declare DMX loops and control zones, solve decoder/driver packing and addressing, and generate the one-line diagram. Experimental — under construction.",
                    "Blank");

                CreateButton(controlsPanel, assemblyPath,
                    "TurboDALI",
                    "     DALI    ",
                    "TurboSuite.Dali.DaliCommand",
                    "Automate DALI addressing for lighting circuits",
                    "Opens the TurboDALI window to group Control Zones into DALI loops, assign each loop its panel ZONE, and assign and write back per-circuit DALI addresses with a job-wide numbering lock. Experimental — under construction.",
                    "Blank");
            }

            // ── Debug panel (TurboSpike) ──
            // Rides the shared ExperimentalCommandsEnabled gate, so the spike bench surfaces every dev
            // session and disappears in shipped builds. Uses the Blank placeholder icon — it's a dev tool.
            if (ExperimentalCommandsEnabled)
            {
                RibbonPanel debugPanel = application.CreateRibbonPanel("TurboSuite", "Debug");
                CreateButton(debugPanel, assemblyPath,
                    "TurboSpike",
                    "    Spike    ",
                    "TurboSuite.Spike.SpikeCommand",
                    "Diagnostic bench — swap the Execute body per investigation",
                    "Runs an ad-hoc diagnostic probe against the running model and shows the result in a dialog. The Execute body is scratch space, swapped out for each investigation. Developer-only; gated off in shipped builds.",
                    "Blank");
            }

            // Auto-update check (two handlers: one checks/stages, one shows the dialog on next idle)
            application.Idling += OnIdlingCheckForUpdate;
            application.Idling += OnIdlingShowUpdateNotification;

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
        if (_updateAccepted && UpdateService.HasStagedUpdate())
        {
            UpdateService.LaunchUpdater();
        }

        TabColoringService.Stop();
        return Result.Succeeded;
    }

    #region Tab Coloring

    private static int _tabStartRetries;

    private static void OnIdlingStartTabColoring(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication uiApp) return;

        _tabStartRetries++;
        bool started = TabColoringService.Start(uiApp.MainWindowHandle, uiApp);

        if (started || _tabStartRetries > 50)
            uiApp.Idling -= OnIdlingStartTabColoring;
    }

    #endregion

    #region Auto-Update

    private static string? _pendingUpdateVersion;
    private static bool _showUpdateNotification;

    // A cold SMB share can take longer than a single check window to wake up at launch, so one
    // timed-out check must not silently skip the update for the whole session. Retry a few times,
    // spaced out, before giving up. All waiting is off the UI thread (async + Task.Delay).
    private const int UpdateCheckMaxAttempts = 3;
    private static readonly TimeSpan UpdateRetryDelay = TimeSpan.FromSeconds(10);

    private static async void OnIdlingCheckForUpdate(object? sender, IdlingEventArgs e)
    {
        if (sender is not UIApplication uiApp) return;

        // One-shot idling handler — the retry loop below handles re-attempts internally.
        uiApp.Idling -= OnIdlingCheckForUpdate;

        try
        {
            // A previously staged (e.g. skipped) update takes priority — no server round-trip.
            if (UpdateService.HasStagedUpdate())
            {
                _pendingUpdateVersion = UpdateService.GetStagedVersion();
            }
            else
            {
                for (int attempt = 1; attempt <= UpdateCheckMaxAttempts; attempt++)
                {
                    using var cts = new CancellationTokenSource(UpdateConstants.CheckTimeoutMs);
                    var result = await UpdateService.CheckForUpdateAsync(cts.Token);

                    if (result.Status == UpdateCheckStatus.UpdateAvailable)
                    {
                        await Task.Run(() => UpdateService.StageUpdate());
                        _pendingUpdateVersion = result.NewVersion;
                        break;
                    }

                    // Reached the server and we're already current — nothing to do.
                    if (result.Status == UpdateCheckStatus.UpToDate)
                        break;

                    // Unavailable (cold share / IO) — wait and retry unless this was the last attempt.
                    if (attempt < UpdateCheckMaxAttempts)
                        await Task.Delay(UpdateRetryDelay);
                }
            }

            if (_pendingUpdateVersion is not null)
                _showUpdateNotification = true;
        }
        catch
        {
            // Update check failed silently — TurboSuite runs normally
        }
    }

    private static void OnIdlingShowUpdateNotification(object? sender, IdlingEventArgs e)
    {
        if (!_showUpdateNotification) return;
        if (sender is not UIApplication uiApp) return;

        _showUpdateNotification = false;
        uiApp.Idling -= OnIdlingShowUpdateNotification;

        try
        {
            var currentVersion = UpdateService.GetInstalledVersion().ToString(3);

            var dialog = new TaskDialog("TurboSuite Update Available")
            {
                MainInstruction = "A new version of TurboSuite is available.",
                MainContent = $"Current version: {currentVersion}\nNew version: {_pendingUpdateVersion}\n\n" +
                              "The update will be applied when you close Revit.\n" +
                              "Please wait a few seconds before reopening Revit to allow the update to complete.",
                CommonButtons = TaskDialogCommonButtons.None
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Update when I close Revit");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Skip this update");

            var result = dialog.Show();

            _updateAccepted = result == TaskDialogResult.CommandLink1;
        }
        catch
        {
            // Don't crash Revit over a notification
        }
    }

    #endregion

    private static void CreateButton(RibbonPanel panel, string assemblyPath,
        string name, string text, string className, string tooltip, string longDescription,
        string iconBaseName)
    {
        PushButtonData buttonData = new PushButtonData(name, text, assemblyPath, className);
        PushButton button = (PushButton)panel.AddItem(buttonData);
        button.ToolTip = tooltip;
        button.LongDescription = longDescription;

        string assembly = Assembly.GetExecutingAssembly().GetName().Name!;
        string largeUri = $"pack://application:,,,/{assembly};component/Icons/{iconBaseName}_32.png";
        string smallUri = $"pack://application:,,,/{assembly};component/Icons/{iconBaseName}_16.png";

        try { button.LargeImage = new BitmapImage(new Uri(largeUri)); } catch { }
        try { button.Image = new BitmapImage(new Uri(smallUri)); } catch { }
    }
}
