#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
// Autodesk.Revit.UI.TextBox is the ribbon control — a probe usually wants the WPF one.
using TextBox = System.Windows.Controls.TextBox;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench.
///
/// PURPOSE: an always-available (in dev) ribbon button whose <see cref="Execute"/> body is meant to be
/// SWAPPED per-investigation. When you need to know something about the running model before writing
/// targeted code — what a parameter's StorageType actually is, whether an API member exists on this
/// Revit version, what a family's connectors/geometry look like — drop a probe here, build, and read
/// the dialog.
///
/// STATE: rides the shared <c>ExperimentalCommandsEnabled</c> gate in <see cref="App.TurboSuiteApplication"/>,
/// so it surfaces every dev session and is gated off in shipped builds — it never reaches production users.
///
/// Keep this ReadOnly and side-effect-free by default. If a probe needs to write, wrap it in a Transaction
/// and change the attribute for the duration of that spike — then revert. This file is scratch space; the
/// body below is only a clean stub — overwrite it freely with whatever probe the current investigation needs.
///
/// NO PROBE LOADED. The last probe (room detection, run 3 — tiebreak rule bake-off) is parked; its source
/// and full write-up live in <c>Specs/RoomDetection_run3_SpikeCommand.cs.txt</c> and
/// <c>Specs/RoomDetection_Investigation_Handoff.md</c>. To resume it, copy that .cs.txt back over this file.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Cancelled;
        }

        ShowReport(commandData,
            "TurboSpike — no probe loaded.\r\n\r\n" +
            "Drop a diagnostic into SpikeCommand.Execute, build, and run it here.\r\n" +
            $"Active document: {uidoc.Document.Title}");
        return Result.Succeeded;
    }

    private static void ShowReport(ExternalCommandData commandData, string text)
    {
        var log = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Text = text
        };

        var copy = new Button
        {
            Content = "Copy all",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        copy.Click += (s, e) => { try { Clipboard.SetText(text); } catch { } };

        var panel = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(copy, Dock.Top);
        panel.Children.Add(copy);
        panel.Children.Add(log);

        var window = new Window
        {
            Title = "TurboSpike",
            Width = 700,
            Height = 400,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
        window.ShowDialog();
    }
}
