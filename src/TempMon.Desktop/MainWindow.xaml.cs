using System.ComponentModel;
using System.Windows;

namespace TempMon.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnStateChanged;
    }

    /// <summary>Closing hides to the tray instead of exiting; the only real teardown is the tray
    /// Exit item (App.ExitApp). With ShutdownMode=OnExplicitShutdown the process keeps running.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    /// <summary>Brings the dashboard back from the tray.</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (ElevationHelper.RelaunchElevated())
            Application.Current.Shutdown();
        // If the user cancels the UAC prompt we simply stay running un-elevated.
    }
}
