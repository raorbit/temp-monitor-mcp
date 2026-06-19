using System.IO;
using System.Text.Json;

namespace TempMon.Mcp;

/// <summary>
/// Finds the desktop app's HTTP base URL by reading the discovery file it writes to
/// %PROGRAMDATA%\TempMon\endpoint.json. Falls back to the default port if the file is missing —
/// this is how the un-elevated MCP server survives the 8757-taken fallback automatically.
/// </summary>
public sealed class EndpointResolver
{
    private static readonly Uri Default = new("http://127.0.0.1:8757");

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TempMon", "endpoint.json");

    public Uri Resolve()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("baseUrl", out var baseUrl) &&
                    baseUrl.GetString() is { } url &&
                    Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Malformed / unreadable file — fall through to the default.
        }

        return Default;
    }
}
