using System.Text.Json;
using Xunit;

namespace TempMon.Mcp.Tests;

/// <summary>
/// Locks the get_temperatures envelope: every parseable snapshot is wrapped as
/// { schema_version, stale, age_seconds, data }, so a consumer always reads the snapshot from
/// <c>.data</c>. A stale one also carries a <c>hint</c>; a missing/unparseable timestamp yields
/// <c>stale=false, age_seconds=null</c>; a non-JSON body is passed through untouched.
/// <c>schema_version</c> is echoed from the snapshot (absent ⇒ 0).
/// </summary>
public sealed class GetTemperaturesStalenessTests
{
    [Fact]
    public async Task Fresh_snapshot_is_wrapped_with_stale_false()
    {
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.FreshSnapshot());
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.False(root.TryGetProperty("hint", out _));   // no hint when fresh

        // The snapshot rides verbatim under .data.
        var data = root.GetProperty("data");
        Assert.Equal("TEST-PC", data.GetProperty("host").GetString());
        Assert.True(data.TryGetProperty("sensors", out _));
    }

    [Fact]
    public async Task Absent_schema_version_defaults_to_zero()
    {
        // Fixtures.Snapshot omits schema_version — the envelope must report 0, not throw or omit it.
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.FreshSnapshot());
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(0, doc.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public async Task Schema_version_is_echoed_from_the_snapshot()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            Fixtures.VersionedSnapshot(Fixtures.TimestampSecondsAgo(0)));
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public async Task Stale_snapshot_is_flagged_with_hint()
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
        Assert.Equal(4, data.GetProperty("sensors").GetArrayLength());
    }

    [Fact]
    public async Task Snapshot_just_under_threshold_is_not_stale()
    {
        // 5s old: comfortably inside the 30s window, so still wrapped but not flagged stale.
        var handler = StubHttpMessageHandler.RespondingWith(
            Fixtures.Snapshot(Fixtures.TimestampSecondsAgo(5)));
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.True(root.GetProperty("age_seconds").GetDouble() < TempMonTools.StaleAfterSeconds);
    }

    [Fact]
    public async Task Missing_timestamp_wraps_with_null_age_and_not_stale()
    {
        // Valid JSON but no 'timestamp' — still wrapped (consumers always read .data), but with no age
        // to compute, age_seconds is null and stale is false (we never manufacture a staleness verdict).
        const string body = """
            { "host": "TEST-PC", "summary": { "cpu_c": 50.0, "gpu_c": 40.0, "max_drive_c": 35.0 }, "sensors": [] }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("age_seconds").ValueKind);
        Assert.Equal("TEST-PC", root.GetProperty("data").GetProperty("host").GetString());
    }

    [Fact]
    public async Task Unparseable_timestamp_wraps_with_null_age()
    {
        // 'timestamp' present but not a date: valid JSON, so still wrapped, but age cannot be computed.
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.Snapshot("not-a-date"));
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("age_seconds").ValueKind);
    }

    [Fact]
    public async Task Non_json_body_is_passed_through_unchanged()
    {
        // A non-JSON body (e.g. an HTML error page) is returned as-is — never wrapped around non-JSON.
        const string body = "<html>503</html>";
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        Assert.Equal(body, result);
    }
}
