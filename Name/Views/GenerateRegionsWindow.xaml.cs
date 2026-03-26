#nullable disable
using System.Windows;

namespace TurboSuite.Name.Views;

public partial class GenerateRegionsWindow : Window
{
    public GenerateRegionsWindow()
    {
        InitializeComponent();
        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Bottom - Height - 20;
    }
}
