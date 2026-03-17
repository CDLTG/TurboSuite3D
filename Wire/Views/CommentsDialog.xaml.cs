using System.Collections.Generic;
using System.Windows;

namespace TurboSuite.Wire.Views;

public partial class CommentsDialog : Window
{
    public string CommentsText { get; private set; } = string.Empty;

    public CommentsDialog(List<string> existingComments, string circuitNumbers = "")
    {
        InitializeComponent();

        if (!string.IsNullOrEmpty(circuitNumbers))
            PromptText.Text = $"Enter circuit ({circuitNumbers}) comment:";

        if (existingComments.Count > 0)
            CommentsComboBox.ItemsSource = existingComments;

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
        DialogResult = true;
    }
}
