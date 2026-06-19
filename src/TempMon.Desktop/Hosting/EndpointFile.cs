using System.IO;
using System.Text.Json;

namespace TempMon.Desktop.Hosting;

/// <summary>
/// Writes the discovery file the non-elevated MCP server reads to find the chosen port.
/// Lives under %PROGRAMDATA%\TempMon\endpoint.json, which is world-readable by default — so the
/// un-elevated reader can see what the elevated writer produced.
/// </summary>
internal static class EndpointFile
{
    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TempMon");

    public static string FilePath => Path.Combine(FolderPath, "endpoint.json");

    public static void Write(Uri baseUrl, bool elevated)
    {
        Directory.CreateDirectory(FolderPath);
        var payload = new
        {
            baseUrl = baseUrl.ToString().TrimEnd('/'),
            port = baseUrl.Port,
            pid = Environment.ProcessId,
            elevated,
            updated = DateTimeOffset.UtcNow.ToString("o"),
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void TryDelete()
    {
        try { File.Delete(FilePath); } catch { /* best effort on shutdown */ }
    }
}
