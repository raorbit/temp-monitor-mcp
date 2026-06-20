using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("TempMon.Mcp.Tests")]

namespace TempMon.Mcp;

/// <summary>
/// Where a <see cref="ResolvedEndpoint"/> came from. Only a <see cref="FromFile"/> endpoint carries
/// a writer pid, so the liveness fast-fail (B4) is gated on this — the default fallback has no pid
/// to probe and must still attempt the HTTP GET.
/// </summary>
internal enum EndpointSource
{
    /// <summary>The discovery file was missing/unreadable — the well-known default port.</summary>
    Default,

    /// <summary>Read from %PROGRAMDATA%\TempMon\endpoint.json, so it may carry the writer's pid.</summary>
    FromFile,
}

/// <summary>
/// The resolved desktop endpoint: its base URL, where it came from, and (when read from the
/// discovery file) the pid of the process that wrote it. The pid lets the MCP server fail fast with
/// a friendly message when the desktop app is gone, instead of waiting out an HTTP timeout.
/// </summary>
internal sealed record ResolvedEndpoint(Uri BaseUrl, EndpointSource Source, int? Pid);

/// <summary>
/// Finds the desktop app's HTTP base URL by reading the discovery file it writes to
/// %PROGRAMDATA%\TempMon\endpoint.json. Falls back to the default port if the file is missing —
/// this is how the un-elevated MCP server survives the 8757-taken fallback automatically.
/// </summary>
public sealed class EndpointResolver
{
    private static readonly Uri Default = new("http://127.0.0.1:8757");

    // Test seam: a pre-baked endpoint that short-circuits the file read, so a test can exercise the
    // FromFile/pid fast-fail without dropping an endpoint.json on disk. Null in production.
    private readonly ResolvedEndpoint? _override;

    public EndpointResolver()
    {
    }

    internal EndpointResolver(ResolvedEndpoint endpoint) => _override = endpoint;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TempMon", "endpoint.json");

    public Uri Resolve() => ResolveEndpoint().BaseUrl;

    /// <summary>Resolves the endpoint with its source and writer pid (when the file supplied one),
    /// so callers can fast-fail on a dead writer before reaching for the network.</summary>
    internal ResolvedEndpoint ResolveEndpoint()
    {
        if (_override is not null) return _override;

        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("baseUrl", out var baseUrl) &&
                    baseUrl.GetString() is { } url &&
                    Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                {
                    int? pid = doc.RootElement.TryGetProperty("pid", out var pidEl) &&
                               pidEl.ValueKind == JsonValueKind.Number &&
                               pidEl.TryGetInt32(out var p)
                        ? p
                        : null;
                    return new ResolvedEndpoint(parsed, EndpointSource.FromFile, pid);
                }
            }
        }
        catch
        {
            // Malformed / unreadable file — fall through to the default.
        }

        return new ResolvedEndpoint(Default, EndpointSource.Default, Pid: null);
    }
}
