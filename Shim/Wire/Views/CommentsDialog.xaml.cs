using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;

namespace TurboSuite.Wire.Views;

public partial class CommentsDialog : Window
{
    public string CommentsText { get; private set; } = string.Empty;
    public string RoomOverrideText { get; private set; } = string.Empty;
    public FamilyInstance? SelectedPanel { get; private set; }

    public CommentsDialog(List<string> existingComments, List<FamilyInstance> panels,
        FamilyInstance? autoSelectedPanel, string circuitNumbers = "",
        string resolvedRoom = "", List<string>? roomNames = null)
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

        PanelComboBox.ItemsSource = panels;
        if (autoSelectedPanel != null)
        {
            // Match by ElementId since objects come from different collectors
            var match = panels.FirstOrDefault(p => p.Id == autoSelectedPanel.Id);
            if (match != null)
                PanelComboBox.SelectedItem = match;
        }
        else if (panels.Count > 0)
            PanelComboBox.SelectedIndex = 0;

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
        SelectedPanel = PanelComboBox.SelectedItem as FamilyInstance;
        DialogResult = true;
    }
}
