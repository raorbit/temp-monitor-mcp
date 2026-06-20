using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace TempMon.Mcp;

/// <summary>
/// The three tools exposed to Claude Code. Each one reaches the desktop app's cached snapshot over
/// localhost HTTP — this process never touches the hardware.
/// </summary>
[McpServerToolType]
public sealed class TempMonTools
{
    // Defaults used by check_thresholds when a caller omits a limit.
    private const double DefaultCpuMax = 80;
    private const double DefaultGpuMax = 75;
    private const double DefaultDriveMax = 60;

    // A snapshot older than this is reported as stale: the desktop polls every 3s, so 30s is ~10
    // missed polls — long enough to rule out a single hiccup, short enough that a wedged poll loop
    // (or a desktop that died after writing endpoint.json) surfaces before a caller trusts the data.
    internal const int StaleAfterSeconds = 30;

    private readonly HttpClient _http;
    private readonly EndpointResolver _endpoint;

    public TempMonTools(HttpClient http, EndpointResolver endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    [McpServerTool(Name = "get_temperatures")]
    [Description("Get the full temperature snapshot — every CPU, GPU, motherboard and storage " +
                 "sensor with current/min/max values — as JSON. Values are null if the desktop " +
                 "app is not running elevated. If the cached snapshot is stale (the desktop's poll " +
                 "loop has stalled) the payload is wrapped with a 'stale' flag and 'age_seconds'.")]
    public async Task<string> GetTemperatures(CancellationToken cancellationToken)
    {
        var (ok, json, error) = await TryFetchAsync(cancellationToken);
        if (!ok) return error!;

        // The snapshot is always wrapped in a { schema_version, stale, age_seconds, data } envelope so
        // a consumer reads from one stable path (.data) whether or not the snapshot turned out stale.
        return Envelope(json!);
    }

    [McpServerTool(Name = "get_summary")]
    [Description("Get just the headline numbers: cpu_c, gpu_c and max_drive_c (°C).")]
    public async Task<string> GetSummary(CancellationToken cancellationToken)
    {
        var (ok, json, error) = await TryFetchAsync(cancellationToken);
        if (!ok) return error!;

        using var doc = JsonDocument.Parse(json!);
        if (SensorsUnavailable(doc.RootElement)) return Unavailable();

        return doc.RootElement.TryGetProperty("summary", out var summary)
            ? summary.GetRawText()
            : Problem("snapshot had no 'summary' field");
    }

    [McpServerTool(Name = "check_thresholds")]
    [Description("List sensors that are currently at or above a temperature limit (°C). Any limit " +
                 "left unset uses a default: CPU 80, GPU 75, drive 60. Motherboard sensors are " +
                 "not checked.")]
    public async Task<string> CheckThresholds(
        [Description("CPU limit in °C (default 80).")] double? cpuMax = null,
        [Description("GPU limit in °C (default 75).")] double? gpuMax = null,
        [Description("Storage/drive limit in °C (default 60).")] double? driveMax = null,
        CancellationToken cancellationToken = default)
    {
        var (ok, json, error) = await TryFetchAsync(cancellationToken);
        if (!ok) return error!;

        double cpuLimit = cpuMax ?? DefaultCpuMax;
        double gpuLimit = gpuMax ?? DefaultGpuMax;
        double driveLimit = driveMax ?? DefaultDriveMax;

        var over = new List<object>();
        using var doc = JsonDocument.Parse(json!);
        if (SensorsUnavailable(doc.RootElement)) return Unavailable();

        if (doc.RootElement.TryGetProperty("sensors", out var sensors) &&
            sensors.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sensors.EnumerateArray())
            {
                var component = s.GetProperty("component").GetString();
                double? limit = component switch
                {
                    "CPU" => cpuLimit,
                    "GPU" => gpuLimit,
                    "Storage" => driveLimit,
                    _ => null,  // motherboard not checked
                };
                if (limit is null) continue;

                if (s.TryGetProperty("value", out var v) &&
                    v.ValueKind == JsonValueKind.Number &&
                    v.GetDouble() is var value && value >= limit)
                {
                    over.Add(new
                    {
                        component,
                        device = s.GetProperty("device").GetString(),
                        name = s.GetProperty("name").GetString(),
                        value,
                        limit,
                    });
                }
            }
        }

        var result = new
        {
            limits = new { cpu_c = cpuLimit, gpu_c = gpuLimit, drive_c = driveLimit },
            over_limit = over,
            count = over.Count,
        };
        return JsonSerializer.Serialize(result);
    }

    private async Task<(bool ok, string? json, string? error)> TryFetchAsync(CancellationToken ct)
    {
        var endpoint = _endpoint.ResolveEndpoint();

        // Fast-fail (B4): when endpoint.json named the desktop's pid, check it's still alive before
        // we wait out an HTTP timeout. We only do this when the file supplied a pid — the default
        // fallback has nothing to probe, so it falls through to the GET as before.
        if (endpoint.Source == EndpointSource.FromFile &&
            endpoint.Pid is { } pid &&
            !IsProcessAlive(pid))
        {
            return (false, null, Problem(
                $"TempMon.Desktop (pid {pid}) is no longer running. " +
                "Start TempMon.Desktop (elevated) and try again."));
        }

        try
        {
            using var response = await _http.GetAsync(new Uri(endpoint.BaseUrl, "/temps"), ct);
            response.EnsureSuccessStatusCode();
            return (true, await response.Content.ReadAsStringAsync(ct), null);
        }
        catch (Exception ex)
        {
            return (false, null, Problem(
                $"could not reach TempMon at {endpoint.BaseUrl} ({ex.Message}). " +
                "Is TempMon.Desktop running (elevated)?"));
        }
    }

    /// <summary>True if a process with this id currently exists. A reused pid can't be told apart
    /// here, but that only risks a slower failure (the HTTP GET), never a wrong fast-fail.</summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
    }

    /// <summary>Always wraps a parseable snapshot in a { schema_version, stale, age_seconds, data }
    /// envelope, so a consumer reads the snapshot from one stable path (<c>.data</c>) regardless of
    /// freshness. <c>stale</c> is <c>true</c> (with a <c>hint</c>) once the snapshot's <c>timestamp</c>
    /// is older than <see cref="StaleAfterSeconds"/>; a missing or unparseable timestamp yields
    /// <c>stale=false, age_seconds=null</c>. <c>schema_version</c> is read from the snapshot (absent
    /// ⇒ 0). A genuinely non-JSON body is passed through untouched — we never wrap non-JSON.</summary>
    private static string Envelope(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return json;   // not JSON at all — never manufacture an envelope around it
        }

        using (doc)
        {
            var root = doc.RootElement;

            int schemaVersion =
                root.TryGetProperty("schema_version", out var sv) &&
                sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var v)
                    ? v : 0;

            double? age = null;
            if (root.TryGetProperty("timestamp", out var ts) &&
                ts.GetString() is { } stamp &&
                DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                age = Math.Round((DateTimeOffset.UtcNow - when).TotalSeconds, 1);
            }

            // Two concrete shapes (rather than one with a conditional key) because this serialize uses
            // the default options, not Snapshot.JsonOptions — a null 'hint' would otherwise be emitted.
            if (age is { } a && a > StaleAfterSeconds)
            {
                return JsonSerializer.Serialize(new
                {
                    schema_version = schemaVersion,
                    stale = true,
                    age_seconds = age,
                    hint = "TempMon.Desktop's poll loop appears stalled — this snapshot is older " +
                           $"than {StaleAfterSeconds}s. Check that TempMon.Desktop is running.",
                    data = root.Clone(),
                });
            }

            return JsonSerializer.Serialize(new
            {
                schema_version = schemaVersion,
                stale = false,
                age_seconds = age,   // a number when the timestamp parsed, null otherwise
                data = root.Clone(),
            });
        }
    }

    /// <summary>True only when the snapshot explicitly reports the sensor reader is closed
    /// (<c>sensors_available: false</c>). A MISSING key means "available" — older desktops, and the
    /// hand-built test fixtures, omit it, and we must never turn their data into a false unavailable.</summary>
    private static bool SensorsUnavailable(JsonElement root) =>
        root.TryGetProperty("sensors_available", out var a) && a.ValueKind == JsonValueKind.False;

    /// <summary>The explicit "sensors could not be read" verdict for the summarising tools, kept
    /// deliberately distinct from <see cref="Problem"/> (a transport failure): the desktop is reachable,
    /// it simply has no readable data — so a zero/all-null answer would be a dangerous false "all clear".</summary>
    private static string Unavailable() => JsonSerializer.Serialize(new
    {
        available = false,
        reason = "sensors_not_readable",
        message = "TempMon.Desktop is running but the hardware sensors could not be read (the driver " +
                  "failed to load, or the app is not elevated). These readings are unavailable — this " +
                  "is NOT a safe 'all clear'. Run TempMon.Desktop elevated and check the elevation banner.",
    });

    /// <summary>A transport/reach failure (could not get a snapshot at all). Carries <c>ok: false</c>
    /// so a consumer can tell it apart from a real payload (none of which carry an <c>ok</c> key) and
    /// from the <see cref="Unavailable"/> verdict (the desktop is reachable but has no readable data).</summary>
    private static string Problem(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message });
}
