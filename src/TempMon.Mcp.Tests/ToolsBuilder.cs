using System.Net.Http;

namespace TempMon.Mcp.Tests;

/// <summary>
/// Wires a <see cref="TempMonTools"/> over a stubbed transport and a pre-baked endpoint, reaching the
/// internal <see cref="EndpointResolver"/> override ctor (visible via InternalsVisibleTo). This is how
/// the B4 fast-fail is exercised without dropping an endpoint.json on disk.
/// </summary>
internal static class ToolsBuilder
{
    private static readonly Uri LoopbackBase = new("http://127.0.0.1:8757");

    /// <summary>Tools whose resolver returns a FromFile endpoint carrying <paramref name="pid"/>.</summary>
    public static TempMonTools FromFile(StubHttpMessageHandler handler, int? pid) =>
        Build(handler, new ResolvedEndpoint(LoopbackBase, EndpointSource.FromFile, pid));

    /// <summary>Tools whose resolver returns the pid-less Default endpoint (no fast-fail probe).</summary>
    public static TempMonTools Default(StubHttpMessageHandler handler) =>
        Build(handler, new ResolvedEndpoint(LoopbackBase, EndpointSource.Default, Pid: null));

    private static TempMonTools Build(StubHttpMessageHandler handler, ResolvedEndpoint endpoint) =>
        new(handler.ToClient(), new EndpointResolver(endpoint));
}
