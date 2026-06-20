using System.Text.Json;
using Xunit;

namespace TempMon.Mcp.Tests;

/// <summary>
/// <c>get_summary</c> returns just the snapshot's 'summary' object raw, an explicit unavailable verdict
/// when the desktop reports its sensors could not be read, or a problem when the field is missing. It
/// does NOT apply the staleness envelope — only get_temperatures does.
/// </summary>
public sealed class GetSummaryTests
{
    [Fact]
    public async Task Returns_the_summary_object_raw()
    {
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.FreshSnapshot());
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetSummary(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal(82.5, root.GetProperty("cpu_c").GetDouble());
        Assert.Equal(60.0, root.GetProperty("gpu_c").GetDouble());
        Assert.Equal(41.0, root.GetProperty("max_drive_c").GetDouble());
    }

    [Fact]
    public async Task Missing_summary_field_returns_problem()
    {
        const string body = """
            { "timestamp": "2026-06-19T00:00:00Z", "host": "TEST-PC", "sensors": [] }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetSummary(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.NotNull(error);
        Assert.Contains("summary", error);
    }

    [Fact]
    public async Task Unavailable_snapshot_returns_explicit_verdict()
    {
        // sensors_available:false — the desktop is up but the reader never opened. get_summary must say
        // so explicitly rather than hand back a summary of nulls that an LLM could read as "all clear".
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.UnavailableSnapshot());
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetSummary(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal("sensors_not_readable", root.GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("cpu_c", out _));
    }

    [Fact]
    public async Task Null_summary_values_survive_unelevated_snapshot()
    {
        // No sensors_available key (missing ⇒ available): a summary whose values are null must still
        // pass through untouched — only an explicit sensors_available:false flips to the unavailable verdict.
        const string body = """
            { "timestamp": "2026-06-19T00:00:00Z", "host": "TEST-PC",
              "summary": { "cpu_c": null, "gpu_c": null, "max_drive_c": null }, "sensors": [] }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(body);
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetSummary(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("cpu_c").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("max_drive_c").ValueKind);
    }
}
