using System.Windows;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// WPF entry point for the NVIDIA GPU monitor.
/// </summary>
public partial class App : Application
{
    [System.STAThread]
    public static void Main()
    {
        var app = new App();
        var window = new MainWindow
        {
            Title = "NVIDIA GPU Monitor",
            Width = 1000,
            Height = 640,
        };
        app.Run(window);
    }
}
