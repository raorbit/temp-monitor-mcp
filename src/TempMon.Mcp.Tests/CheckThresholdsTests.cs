using System.Linq;
using System.Text.Json;
using Xunit;

namespace TempMon.Mcp.Tests;

/// <summary>
/// <c>check_thresholds</c> reports sensors at or above their limit. Defaults are CPU 80 / GPU 75 /
/// drive 60; callers can override each. Motherboard sensors are never checked even when scorching.
/// </summary>
public sealed class CheckThresholdsTests
{
    private static async Task<JsonDocument> RunWithFixture(
        double? cpuMax = null, double? gpuMax = null, double? driveMax = null)
    {
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.FreshSnapshot());
        var tools = ToolsBuilder.Default(handler);
        var json = await tools.CheckThresholds(cpuMax, gpuMax, driveMax, CancellationToken.None);
        return JsonDocument.Parse(json);
    }

    [Fact]
    public async Task Defaults_flag_only_the_cpu_over_eighty()
    {
        // Fixture: CPU 82.5 (>80), GPU 60 (<75), Storage 41 (<60), Motherboard 95 (ignored).
        using var doc = await RunWithFixture();
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("count").GetInt32());

        var over = root.GetProperty("over_limit");
        Assert.Equal(1, over.GetArrayLength());

        var hit = over[0];
        Assert.Equal("CPU", hit.GetProperty("component").GetString());
        Assert.Equal(82.5, hit.GetProperty("value").GetDouble());
        Assert.Equal(80.0, hit.GetProperty("limit").GetDouble());
    }

    [Fact]
    public async Task Echoes_the_effective_limits()
    {
        using var doc = await RunWithFixture();
        var limits = doc.RootElement.GetProperty("limits");

        Assert.Equal(80.0, limits.GetProperty("cpu_c").GetDouble());
        Assert.Equal(75.0, limits.GetProperty("gpu_c").GetDouble());
        Assert.Equal(60.0, limits.GetProperty("drive_c").GetDouble());
    }

    [Fact]
    public async Task Lower_gpu_limit_brings_the_gpu_over()
    {
        // Drop the GPU limit under the 60° fixture value — now both CPU and GPU should flag.
        using var doc = await RunWithFixture(gpuMax: 55);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("count").GetInt32());

        var components = root.GetProperty("over_limit")
            .EnumerateArray()
            .Select(e => e.GetProperty("component").GetString())
            .ToHashSet();
        Assert.Contains("CPU", components);
        Assert.Contains("GPU", components);
    }

    [Fact]
    public async Task Hot_motherboard_is_never_flagged()
    {
        // Even with every limit dropped to 0, the 95° Motherboard sensor stays out of the results.
        using var doc = await RunWithFixture(cpuMax: 0, gpuMax: 0, driveMax: 0);

        var components = doc.RootElement.GetProperty("over_limit")
            .EnumerateArray()
            .Select(e => e.GetProperty("component").GetString())
            .ToList();

        Assert.DoesNotContain("Motherboard", components);
        // CPU, GPU and Storage all clear 0 — three hits, the motherboard excluded.
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task At_exactly_the_limit_counts_as_over()
    {
        // The check is value >= limit, so a CPU exactly at 82.5 must flag.
        using var doc = await RunWithFixture(cpuMax: 82.5);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Null_sensor_values_are_skipped()
    {
        // Unelevated snapshot: every value is null, so nothing can be over any limit.
        const string body = """
            { "timestamp": "2026-06-19T00:00:00Z", "host": "TEST-PC",
              "summary": { "cpu_c": null, "gpu_c": null, "max_drive_c": null },
              "sensors": [
                { "component": "CPU", "device": "d", "name": "n", "value": null, "min": null, "max": null }
              ] }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var json = await tools.CheckThresholds(null, null, null, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }
}
