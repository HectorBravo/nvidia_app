using System.Windows;
using System.Windows.Controls;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// Placeholder main window (Subagent B replaces with the full UI).
/// </summary>
public class MainWindow : Window
{
    public MainWindow()
    {
        Content = new TextBlock
        {
            Text = "NVIDIA GPU Monitor — UI under construction",
            FontSize = 20,
            Margin = new System.Windows.Thickness(16),
        };
    }
}
