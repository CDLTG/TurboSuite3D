using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;

namespace TurboSuite.Shared.Views;

/// <summary>
/// The shared circuit-info dialog: circuit comment, room override, and panel assignment,
/// shown after a command creates or wires a circuit (TurboWire, TurboDriver). Gated by the
/// General setting; see <c>TurboSuite.Shared.Services.CircuitInfoService</c> for the
/// resolve-defaults / apply pipeline. (Formerly <c>TurboSuite.Wire.Views.CommentsDialog</c>.)
/// </summary>
public partial class CircuitInfoDialog : Window
{
    /// <summary>
    /// A panel dropdown entry. <see cref="Panel"/> is null for the "&lt;None&gt;" choice
    /// (DMX/DALI and other circuits that never live on a distribution board).
    /// </summary>
    public sealed class PanelChoice
    {
        public string Name { get; }
        public FamilyInstance? Panel { get; }

        public PanelChoice(FamilyInstance panel)
        {
            Panel = panel;
            Name = panel.Name;
        }

        private PanelChoice(string name) => Name = name;

        public static readonly PanelChoice None = new("<None>");
    }

    public string CommentsText { get; private set; } = string.Empty;
    public string RoomOverrideText { get; private set; } = string.Empty;

    /// <summary>
    /// Prefills the comment field with the circuit's current (shared) comment so a re-wired,
    /// already-commented circuit shows its comment rather than a blank box. Set via object
    /// initializer after construction; blank leaves the field empty.
    /// </summary>
    public string CommentsPrefill
    {
        set => CommentsComboBox.Text = value ?? string.Empty;
    }
    public FamilyInstance? SelectedPanel { get; private set; }

    /// <summary>True when the user explicitly picked "&lt;None&gt;" — the circuit should
    /// be unassigned from any panel, not left on its auto-assigned one.</summary>
    public bool UnassignPanel { get; private set; }

    public CircuitInfoDialog(List<string> existingComments, List<FamilyInstance> panels,
        FamilyInstance? autoSelectedPanel, string circuitNumbers = "",
        string resolvedRoom = "", List<string>? roomNames = null, bool defaultToNone = false)
    {
        InitializeComponent();

        if (!string.IsNullOrEmpty(circuitNumbers))
            PromptText.Text = $"Enter circuit ({circuitNumbers}) comment:";

        if (existingComments.Count > 0)
            CommentsComboBox.ItemsSource = existingComments;

        // Room Override: dropdown offers existing project room names for
        // search/autofill; the text is prefilled with the live base room so the
        // user sees what will be used unless they override it (blank is valid).
        if (roomNames != null && roomNames.Count > 0)
            RoomOverrideComboBox.ItemsSource = roomNames;
        RoomOverrideComboBox.Text = resolvedRoom ?? string.Empty;

        // "<None>" leads the list so circuits that never live on a panel can be
        // created unassigned; real panels follow.
        var choices = new List<PanelChoice> { PanelChoice.None };
        choices.AddRange(panels.Select(p => new PanelChoice(p)));
        PanelComboBox.ItemsSource = choices;

        if (defaultToNone)
        {
            // The previous circuit was deliberately left unassigned — carry that forward.
            PanelComboBox.SelectedItem = PanelChoice.None;
        }
        else
        {
            PanelChoice? match = autoSelectedPanel != null
                // Match by ElementId since objects come from different collectors
                ? choices.FirstOrDefault(c => c.Panel?.Id == autoSelectedPanel.Id)
                : null;
            // Default to the auto-selected panel, else the first real panel; fall back to
            // <None> only when there are no panels at all.
            PanelComboBox.SelectedItem = match
                ?? choices.FirstOrDefault(c => c.Panel != null)
                ?? PanelChoice.None;
        }

        Loaded += (_, _) =>
        {
            // Focus the editable text portion of the ComboBox
            var textBox = CommentsComboBox.Template.FindName("PART_EditableTextBox",
                CommentsComboBox) as System.Windows.Controls.TextBox;
            textBox?.Focus();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        CommentsText = CommentsComboBox.Text;
        RoomOverrideText = RoomOverrideComboBox.Text;
        var choice = PanelComboBox.SelectedItem as PanelChoice;
        SelectedPanel = choice?.Panel;
        UnassignPanel = choice != null && choice.Panel == null;
        DialogResult = true;
    }
}
