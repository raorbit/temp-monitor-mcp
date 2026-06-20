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
/// this is how the un-elevated MCP server survives the 8757-taken fallback automatically. The file
/// lives under a world-writable directory, so the resolver trusts its <c>baseUrl</c> only when it is
/// an http/https loopback address (see <see cref="TryParseEndpoint"/>).
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
            if (File.Exists(FilePath) && TryParseEndpoint(File.ReadAllText(FilePath), out var endpoint))
                return endpoint;
        }
        catch
        {
            // Malformed / unreadable file — fall through to the default.
        }

        return new ResolvedEndpoint(Default, EndpointSource.Default, Pid: null);
    }

    /// <summary>
    /// Parses the discovery file's JSON, accepting its <c>baseUrl</c> ONLY when it is an http/https
    /// loopback address. <c>endpoint.json</c> lives under world-writable <c>%PROGRAMDATA%</c>, so a
    /// standard-user process could plant a <c>baseUrl</c> pointing off-box (to feed the agent false
    /// temperatures) or at another local service (a loopback SSRF). We refuse any non-loopback or
    /// non-http(s) URL and let the caller fall back to the well-known loopback default. Returns
    /// <c>false</c> (no endpoint) when the JSON has no usable, loopback <c>baseUrl</c>; the caller's
    /// try/catch handles a genuinely malformed file.
    /// </summary>
    internal static bool TryParseEndpoint(string json, out ResolvedEndpoint endpoint)
    {
        endpoint = null!;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("baseUrl", out var baseUrl) &&
            baseUrl.GetString() is { } url &&
            Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            parsed.IsLoopback)
        {
            int? pid = doc.RootElement.TryGetProperty("pid", out var pidEl) &&
                       pidEl.ValueKind == JsonValueKind.Number &&
                       pidEl.TryGetInt32(out var p)
                ? p
                : null;
            endpoint = new ResolvedEndpoint(parsed, EndpointSource.FromFile, pid);
            return true;
        }

        return false;
    }
}
