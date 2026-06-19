using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace TempMon.Desktop;

internal static class ElevationHelper
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Relaunches this exe with a UAC elevation prompt. Returns false if the user
    /// declined the prompt (caller should stay running, un-elevated).</summary>
    public static bool RelaunchElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false; // user cancelled the UAC dialog
        }
    }
}
