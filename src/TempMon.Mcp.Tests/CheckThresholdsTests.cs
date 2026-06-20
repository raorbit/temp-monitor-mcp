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
    public async Task Unavailable_snapshot_returns_explicit_verdict_not_count_zero()
    {
        // The headline P1: a desktop whose reader never opened (sensors_available:false) must NOT come
        // back as count:0 ("nothing over limit") — it must say the sensors are unavailable.
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.UnavailableSnapshot());
        var tools = ToolsBuilder.Default(handler);

        var json = await tools.CheckThresholds(null, null, null, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal("sensors_not_readable", root.GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("count", out _));
    }

    [Fact]
    public async Task Null_sensor_values_are_skipped()
    {
        // No sensors_available key (missing ⇒ available): null-valued sensors are simply skipped and
        // the count is 0 — distinct from the explicit unavailable verdict above.
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

    [Fact]
    public async Task Partial_snapshot_flags_unreadable_cpu_and_never_reads_as_all_clear()
    {
        // Issue #1: un-elevated, the CPU sensor is readable:false with a real-looking 0. It must NOT clear
        // the limit silently — the result carries partial:true + unreadable:["CPU"], and the 0 is no hit.
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.PartialSnapshot());
        var tools = ToolsBuilder.Default(handler);

        var json = await tools.CheckThresholds(null, null, null, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("partial").GetBoolean());
        var unreadable = root.GetProperty("unreadable").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("CPU", unreadable);
        // The GPU (NVML) and cool Storage (SMART) read fine and are never marked unreadable.
        Assert.DoesNotContain("GPU", unreadable);
        Assert.DoesNotContain("Storage", unreadable);

        // The zeroed CPU is excluded from over_limit (never treated as 0 < 80 = "fine").
        var over = root.GetProperty("over_limit").EnumerateArray()
            .Select(e => e.GetProperty("component").GetString()).ToList();
        Assert.DoesNotContain("CPU", over);
        Assert.Empty(over);
        // count:0 here, but partial:true is present — so it can't read as a clean "all clear".
        Assert.Equal(0, root.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task All_readable_fixture_carries_no_partial_or_unreadable_keys()
    {
        // Back-compat: the standard fixture omits 'readable' (missing ⇒ readable), so the result is the
        // plain shape with no partial/unreadable keys — behaviour identical to before the fix.
        using var doc = await RunWithFixture();
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("partial", out _));
        Assert.False(root.TryGetProperty("unreadable", out _));
    }

    [Fact]
    public async Task Readable_null_cpu_stays_count_zero_with_no_partial()
    {
        // The distinction: a sensor that is readable:true but simply has no value (null) is skipped →
        // count 0 with NO partial. Only readable:false (privilege-gated, unknown) escalates to partial.
        const string body = """
            { "timestamp": "2026-06-19T00:00:00Z", "host": "TEST-PC", "sensors_available": true,
              "summary": { "cpu_c": null, "gpu_c": null, "max_drive_c": null },
              "sensors": [
                { "component": "CPU", "device": "d", "name": "n", "value": null, "min": null, "max": null, "readable": true }
              ] }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var json = await tools.CheckThresholds(null, null, null, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.False(root.TryGetProperty("partial", out _));
    }
}
