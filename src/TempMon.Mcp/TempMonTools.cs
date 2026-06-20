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

        // Fresh snapshots return the wire contract verbatim; only a stale one gets wrapped, so a
        // caller that doesn't care about staleness still sees the exact payload it expects.
        return MarkStaleIfNeeded(json!);
    }

    [McpServerTool(Name = "get_summary")]
    [Description("Get just the headline numbers: cpu_c, gpu_c and max_drive_c (°C).")]
    public async Task<string> GetSummary(CancellationToken cancellationToken)
    {
        var (ok, json, error) = await TryFetchAsync(cancellationToken);
        if (!ok) return error!;

        using var doc = JsonDocument.Parse(json!);
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

    /// <summary>Wraps the snapshot in a staleness envelope when its <c>timestamp</c> is older than
    /// <see cref="StaleAfterSeconds"/>; otherwise returns the payload untouched. A snapshot whose
    /// timestamp is missing or unparseable is passed through — we don't manufacture staleness.</summary>
    private static string MarkStaleIfNeeded(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("timestamp", out var ts) &&
                ts.GetString() is { } stamp &&
                DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                double age = (DateTimeOffset.UtcNow - when).TotalSeconds;
                if (age > StaleAfterSeconds)
                {
                    var envelope = new
                    {
                        stale = true,
                        age_seconds = Math.Round(age, 1),
                        hint = "TempMon.Desktop's poll loop appears stalled — this snapshot is older " +
                               $"than {StaleAfterSeconds}s. Check that TempMon.Desktop is running.",
                        data = doc.RootElement.Clone(),
                    };
                    return JsonSerializer.Serialize(envelope);
                }
            }
        }
        catch
        {
            // Unparseable payload — return it as-is rather than inventing a staleness verdict.
        }

        return json;
    }

    private static string Problem(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
