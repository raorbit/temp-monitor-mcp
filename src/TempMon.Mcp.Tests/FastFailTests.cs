using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace TempMon.Mcp.Tests;

/// <summary>
/// Locks the B4 fast-fail: when endpoint.json supplied the writer pid (FromFile) and that process is
/// gone, the tools return a friendly "not running" error WITHOUT waiting out an HTTP timeout. The
/// stub handler's RequestCount is the proof no GET was attempted.
/// </summary>
public sealed class FastFailTests
{
    // int.MaxValue is a pid no real process will hold — Process.GetProcessById throws ArgumentException.
    private const int DeadPid = int.MaxValue;

    [Fact]
    public async Task FromFile_with_dead_pid_fails_fast_without_any_http_call()
    {
        // The handler would happily return a fresh body, but the fast-fail must short-circuit first.
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.FreshSnapshot());
        var tools = ToolsBuilder.FromFile(handler, pid: DeadPid);

        var result = await tools.GetTemperatures(CancellationToken.None);

        Assert.Equal(0, handler.RequestCount);

        using var doc = JsonDocument.Parse(result);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.NotNull(error);
        Assert.Contains(DeadPid.ToString(), error);
        Assert.Contains("no longer running", error);
    }

    [Fact]
    public async Task FromFile_with_live_pid_proceeds_to_http()
    {
        // Environment.ProcessId is guaranteed alive — the liveness check passes and the GET happens.
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.FreshSnapshot());
        var tools = ToolsBuilder.FromFile(handler, pid: Environment.ProcessId);

        var result = await tools.GetTemperatures(CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        Assert.EndsWith("/temps", handler.LastRequestUri!.AbsolutePath);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("sensors", out _));
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Default_source_has_no_pid_and_still_attempts_http()
    {
        // The default fallback carries no pid, so there is nothing to probe — it must GET as before.
        var handler = StubHttpMessageHandler.RespondingWith(Fixtures.FreshSnapshot());
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("sensors", out _));
    }

    [Fact]
    public async Task Unreachable_desktop_returns_friendly_error()
    {
        // Connection-refused style failure (desktop not running, default endpoint, no pid to probe).
        var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("Connection refused"));
        var tools = ToolsBuilder.Default(handler);

        var result = await tools.GetTemperatures(CancellationToken.None);

        using var doc = JsonDocument.Parse(result);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.NotNull(error);
        Assert.Contains("could not reach TempMon", error);
    }
}
