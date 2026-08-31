#nullable disable
using System.Windows;

namespace TurboSuite.Setup.Views;

/// <summary>
/// TurboSetup launcher — a single fixed-size, suite-styled window that swaps its content as the user
/// clicks through, replacing the old native TaskDialog menus. Page 1 routes to Project Setup (which
/// runs its existing wizard unchanged) or forward to Page 2; Page 2 offers the Name-Spaces choice.
/// The command reads <see cref="Choice"/> after <c>ShowDialog</c> returns.
/// </summary>
public partial class TurboSetupLandingWindow : Window
{
    public enum SetupChoice
    {
        Cancel,
        ProjectSetup,
        NameSpacesBlankOnly,
        NameSpacesForce
    }

    public SetupChoice Choice { get; private set; } = SetupChoice.Cancel;

    public TurboSetupLandingWindow()
    {
        InitializeComponent();
    }

    private void ProjectSetup_Click(object sender, RoutedEventArgs e)
    {
        Choice = SetupChoice.ProjectSetup;
        DialogResult = true;
        Close();
    }

    private void NameSpaces_Click(object sender, RoutedEventArgs e) => ShowNameSpacesPage();

    private void Back_Click(object sender, RoutedEventArgs e) => ShowLandingPage();

    private void NameBlankOnly_Click(object sender, RoutedEventArgs e)
    {
        Choice = SetupChoice.NameSpacesBlankOnly;
        DialogResult = true;
        Close();
    }

    private void NameForce_Click(object sender, RoutedEventArgs e)
    {
        Choice = SetupChoice.NameSpacesForce;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowLandingPage()
    {
        HeaderSubtitle.Text = "Choose a setup action.";
        PanelLanding.Visibility = Visibility.Visible;
        PanelNameSpaces.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
    }

    private void ShowNameSpacesPage()
    {
        HeaderSubtitle.Text = "Name Spaces from architect Rooms.";
        PanelLanding.Visibility = Visibility.Collapsed;
        PanelNameSpaces.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
    }
}
