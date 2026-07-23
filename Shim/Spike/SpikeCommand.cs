#nullable disable
using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
// Autodesk.Revit.UI.TextBox is the ribbon control — this probe wants the WPF one.
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
/// CURRENT PROBE — does the Revit SELECTION survive the modeless round-trip? (TurboName Clear &amp; Regenerate.)
/// Question: TurboName's planned "Clear selected &amp; regenerate" mode reads
/// <c>uidoc.Selection.GetElementIds()</c> from inside its external-event handler, at a moment when the user
/// has (a) selected regions in the view, then (b) clicked into the modeless WPF window to press a button.
/// Does the selection still exist at that point, or does the focus change to the window clear it?
///
/// This probe replicates that exact architecture — a modeless window whose button raises an ExternalEvent
/// whose handler reads the selection — rather than reading it from a modal command, because a modal command
/// never gives up focus and so cannot answer the question.
///
/// Read-only: reports the selection, changes nothing.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    private static Window _activeWindow;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (_activeWindow != null) { _activeWindow.Activate(); return Result.Succeeded; }

        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Cancelled;
        }

        var log = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Text = "1. Select some filled regions in the view.\r\n"
                 + "2. Click into THIS window (that's the focus change under test).\r\n"
                 + "3. Press \"Read selection\".\r\n"
                 + "4. Repeat after clicking the view again, to compare.\r\n"
                 + new string('=', 64) + "\r\n"
        };

        var handler = new SelectionProbeHandler(uidoc, line =>
            log.Dispatcher.Invoke(() => { log.AppendText(line); log.ScrollToEnd(); }));
        var externalEvent = ExternalEvent.Create(handler);

        var button = new Button { Content = "Read selection", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (s, e) => externalEvent.Raise();

        var panel = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(button, Dock.Top);
        panel.Children.Add(button);
        panel.Children.Add(log);

        var window = new Window
        {
            Title = "TurboSpike — selection survival probe",
            Width = 560,
            Height = 420,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
        window.Closed += (s, e) => { _activeWindow = null; externalEvent.Dispose(); };
        _activeWindow = window;
        window.Show();

        return Result.Succeeded;
    }

    /// <summary>
    /// Reads the selection from inside a valid API context — the same place TurboNameApiHandler would.
    /// Reports the count, and for each element enough to tell a clearable Room Region from anything else.
    /// </summary>
    private class SelectionProbeHandler : IExternalEventHandler
    {
        private readonly UIDocument _uidoc;
        private readonly Action<string> _report;
        private int _n;

        public SelectionProbeHandler(UIDocument uidoc, Action<string> report)
        {
            _uidoc = uidoc;
            _report = report;
        }

        public string GetName() => "TurboSpike selection probe";

        public void Execute(UIApplication app)
        {
            _n++;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"── READ #{_n} @ {DateTime.Now:HH:mm:ss} ───────────────────");

            try
            {
                var doc = _uidoc.Document;
                var ids = _uidoc.Selection.GetElementIds();
                sb.AppendLine($"  GetElementIds().Count = {ids.Count}"
                            + (ids.Count == 0 ? "   *** EMPTY — selection did NOT survive ***" : ""));

                foreach (var id in ids)
                {
                    var el = doc.GetElement(id);
                    if (el == null) { sb.AppendLine($"    {id} → (null element)"); continue; }

                    string typeName = "(none)";
                    var typeId = el.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                        typeName = doc.GetElement(typeId)?.Name ?? "(null type)";

                    sb.AppendLine($"    {id}  cat={el.Category?.Name ?? "(null)"}"
                                + $"  class={el.GetType().Name}  type=\"{typeName}\"");

                    if (el is FilledRegion fr)
                        sb.AppendLine($"        FilledRegion  IsMasking={fr.IsMasking}  ownerView={fr.OwnerViewId}");
                }

                // The production code intersects the selection against the view's clearable regions —
                // confirm that intersection is non-empty for the same picks.
                int clearable = ids.Select(doc.GetElement)
                    .OfType<FilledRegion>()
                    .Count(fr => !fr.IsMasking && fr.OwnerViewId == doc.ActiveView.Id);
                sb.AppendLine($"  → filled regions owned by the active view: {clearable}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  THREW: {ex.GetType().Name}: {ex.Message}");
            }

            _report(sb.ToString());
        }
    }
}
