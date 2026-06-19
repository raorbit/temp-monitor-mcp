using System.ComponentModel;
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
                 "app is not running elevated.")]
    public async Task<string> GetTemperatures(CancellationToken cancellationToken)
    {
        var (ok, json, error) = await TryFetchAsync(cancellationToken);
        return ok ? json! : error!;
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
        var baseUrl = _endpoint.Resolve();
        try
        {
            using var response = await _http.GetAsync(new Uri(baseUrl, "/temps"), ct);
            response.EnsureSuccessStatusCode();
            return (true, await response.Content.ReadAsStringAsync(ct), null);
        }
        catch (Exception ex)
        {
            return (false, null, Problem(
                $"could not reach TempMon at {baseUrl} ({ex.Message}). " +
                "Is TempMon.Desktop running (elevated)?"));
        }
    }

    private static string Problem(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
