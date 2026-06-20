using System.Text.Json;
using Xunit;

namespace TempMon.Mcp.Tests;

/// <summary>
/// Locks the C1 staleness behavior of <c>get_temperatures</c>: fresh snapshots come back verbatim,
/// stale ones get the { stale, age_seconds, hint, data } envelope, and anything with an
/// unusable timestamp is passed through untouched (we never manufacture a staleness verdict).
/// </summary>
public sealed class GetTemperaturesStalenessTests
{
    [Fact]
    public async Task Fresh_snapshot_is_returned_verbatim()
    {
        var body = Fixtures.FreshSnapshot();
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        // The wire contract is returned unchanged — no envelope, byte-for-byte the body.
        Assert.Equal(body, result);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.TryGetProperty("stale", out _));
        Assert.True(doc.RootElement.TryGetProperty("sensors", out _));
    }

    [Fact]
    public async Task Stale_snapshot_is_wrapped_in_envelope()
    {
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.StaleSnapshot(ageSeconds: 120));
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("stale").GetBoolean());
        Assert.True(root.GetProperty("age_seconds").GetDouble() > TempMonTools.StaleAfterSeconds);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("hint").GetString()));

        // The original payload is nested verbatim under 'data'.
        var data = root.GetProperty("data");
        Assert.Equal("TEST-PC", data.GetProperty("host").GetString());
        Assert.True(data.TryGetProperty("sensors", out var sensors));
        Assert.Equal(4, sensors.GetArrayLength());
    }

    [Fact]
    public async Task Snapshot_just_under_threshold_is_not_marked_stale()
    {
        // 5s old: comfortably inside the 30s window, so no envelope.
        var body = Fixtures.Snapshot(Fixtures.TimestampSecondsAgo(5));
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task Missing_timestamp_is_passed_through_unchanged()
    {
        // No 'timestamp' field — the tool must not invent staleness; it returns the body as-is.
        const string body = """
            { "host": "TEST-PC", "summary": { "cpu_c": 50.0, "gpu_c": 40.0, "max_drive_c": 35.0 }, "sensors": [] }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task Unparseable_timestamp_is_passed_through_unchanged()
    {
        var body = Fixtures.Snapshot("not-a-date");
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        Assert.Equal(body, result);
    }
}
