using System.Diagnostics;
using System.Security.Principal;

namespace TempMon.Desktop.Autostart;

/// <summary>
/// Creates/removes/queries the auto-start entry by shelling <c>schtasks.exe</c> (in-box, no NuGet).
///
/// The task is a per-user ONLOGON job with Run Level Highest. That run level IS the elevation grant,
/// so the requireAdministrator exe launches already-elevated with no UAC prompt at logon — unlike an
/// HKCU Run entry, which would prompt (or be blocked) every logon. Creating/deleting needs admin,
/// which the app already has. <see cref="Microsoft.Win32.TaskScheduler"/> COM is the upgrade path
/// for richer task XML, but schtasks keeps the dependency surface at zero for three commands.
/// </summary>
internal static class AutoStartManager
{
    private const string TaskName = "TempMon Autostart";

    /// <summary>True when the scheduled task exists (schtasks /Query exits 0).</summary>
    public static bool IsEnabled() =>
        Run($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Creates (or overwrites, via /F) the ONLOGON / Highest task pointing at this exe.</summary>
    public static bool Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var user = WindowsIdentity.GetCurrent().Name;

        // /TR is itself a quoted string; the inner exe path is quoted (\") to survive spaces, and
        // --autostart tells the launched app to start hidden to tray.
        var tr = $"\\\"{exe}\\\" --autostart";
        return Run(
            $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /RU \"{user}\" /TR \"{tr}\" /F") == 0;
    }

    /// <summary>Removes the scheduled task.</summary>
    public static bool Disable() =>
        Run($"/Delete /TN \"{TaskName}\" /F") == 0;

    /// <summary>Runs schtasks with the given args, swallows stdout/stderr, returns the exit code
    /// (-1 if the process could not even start).</summary>
    private static int Run(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
