using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hardcodet.Wpf.TaskbarNotification;
using TempMon.Desktop.ViewModels;

namespace TempMon.Desktop.Tray;

/// <summary>
/// Builds and wires the Hardcodet <see cref="TaskbarIcon"/> from code: programmatic gradient icon,
/// the dark <see cref="TrayResources"/> flyout, tooltip bound to the VM, and the three flyout
/// actions routed to App methods. The flyout shares the single <see cref="DashboardViewModel"/>,
/// so its CPU header/check glyph track the live poll without a second update path.
/// </summary>
internal static class TrayIconFactory
{
    public static TaskbarIcon Create(
        DashboardViewModel vm, Action openDashboard, Action exit, Action toggleAutostart)
    {
        var resources = (ResourceDictionary)Application.LoadComponent(
            new Uri("/TempMon.Desktop;component/Tray/TrayResources.xaml", UriKind.Relative));
        var template = (ControlTemplate)resources["TrayFlyout"];

        // The TrayPopup is a templated ContentControl whose DataContext is the shared VM.
        var popup = new ContentControl
        {
            Template = template,
            DataContext = vm,
        };
        // Force the template to expand now so the named rows exist for wiring.
        popup.ApplyTemplate();

        Wire(template, popup, "OpenRow", openDashboard);
        Wire(template, popup, "AutoStartRow", toggleAutostart);
        Wire(template, popup, "ExitRow", exit);

        var icon = new TaskbarIcon
        {
            DataContext = vm,
            IconSource = TrayIcon.BuildLogo(),
            TrayPopup = popup,
            ToolTipText = vm.TrayTooltip,
        };

        // Keep the native tooltip in sync with the VM (ToolTipText is a plain CLR property).
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.TrayTooltip))
                icon.ToolTipText = vm.TrayTooltip;
        };

        // Double-clicking the tray icon opens the dashboard.
        icon.TrayMouseDoubleClick += (_, _) => openDashboard();

        return icon;
    }

    private static void Wire(ControlTemplate template, ContentControl popup, string name, Action action)
    {
        if (template.FindName(name, popup) is FrameworkElement row)
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                action();
            };
    }
}
