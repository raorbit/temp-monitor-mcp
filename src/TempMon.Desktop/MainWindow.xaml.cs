using System.Windows;

namespace TempMon.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (ElevationHelper.RelaunchElevated())
            Application.Current.Shutdown();
        // If the user cancels the UAC prompt we simply stay running un-elevated.
    }
}
