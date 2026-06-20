using System.IO;
using Xunit;

namespace TempMon.Mcp.Tests;

/// <summary>
/// The override-ctor seam the B4 tests rely on, plus the resolver's defaulting: a malformed/missing
/// discovery file must fall back to the well-known port as a pid-less Default endpoint, so the
/// un-elevated server keeps working after the 8757-taken fallback.
/// </summary>
public sealed class EndpointResolverTests
{
    [Fact]
    public void Override_ctor_returns_the_baked_endpoint()
    {
        var baked = new ResolvedEndpoint(new Uri("http://127.0.0.1:9999"), EndpointSource.FromFile, Pid: 4242);
        var resolver = new EndpointResolver(baked);

        var resolved = resolver.ResolveEndpoint();

        Assert.Same(baked, resolved);
        Assert.Equal(EndpointSource.FromFile, resolved.Source);
        Assert.Equal(4242, resolved.Pid);
        Assert.Equal(new Uri("http://127.0.0.1:9999"), resolver.Resolve());
    }

    [Fact]
    public void Parameterless_resolver_falls_back_to_default_when_no_file()
    {
        // Real machines may or may not have an endpoint.json; only assert the file-absent contract.
        if (File.Exists(EndpointResolver.FilePath))
            return; // a live desktop wrote a file — skip rather than assert against its contents.

        var resolved = new EndpointResolver().ResolveEndpoint();

        Assert.Equal(EndpointSource.Default, resolved.Source);
        Assert.Null(resolved.Pid);
        Assert.Equal("http://127.0.0.1:8757/", resolved.BaseUrl.ToString());
    }
}
