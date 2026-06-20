using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TempMon.Core;
using TempMon.Desktop.Hosting;
using Xunit;

namespace TempMon.Desktop.Tests;

/// <summary>
/// Stands up the in-process Kestrel server against a real SensorPoller and asserts the /temps and
/// /health wire shapes. On an un-elevated runner the poller can't open the driver, so the snapshot is
/// empty — we assert the JSON SHAPE (keys present, sensors is an array), not the component vocabulary,
/// which only appears on an elevated box. This is route/serialization coverage, not sensor coverage.
/// </summary>
public sealed class TempServerRouteTests
{
    [Fact]
    public async Task Temps_route_is_byte_identical_to_the_cached_snapshot()
    {
        using var poller = new SensorPoller(elevated: false);
        await using var server = new TempServer(poller, elevated: false);
        var baseUrl = await server.StartAsync();

        using var http = new HttpClient();
        var body = await http.GetStringAsync(new Uri(baseUrl, "/temps"));

        // /temps serves SensorPoller.Latest verbatim — nothing polls here, so it is stable.
        Assert.Equal(poller.Latest.ToJson(), body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("schema_version").ValueKind);
        Assert.True(root.GetProperty("sensors_available").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.True(root.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("cpu_c", out _));
        Assert.True(summary.TryGetProperty("gpu_c", out _));
        Assert.True(summary.TryGetProperty("max_drive_c", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sensors").ValueKind);
    }

    [Fact]
    public async Task Health_route_reports_ok_and_the_contract_fields()
    {
        using var poller = new SensorPoller(elevated: false);
        await using var server = new TempServer(poller, elevated: false);
        var baseUrl = await server.StartAsync();

        using var http = new HttpClient();
        var body = await http.GetStringAsync(new Uri(baseUrl, "/health"));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(root.GetProperty("elevated").GetBoolean());   // constructed with elevated: false
        Assert.Equal(Snapshot.CurrentSchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.True(root.GetProperty("sensors_available").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("snapshot_at").GetString()));
    }
}
