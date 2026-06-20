using System.IO;
using Xunit;

namespace TempMon.Mcp.Tests;

/// <summary>
/// The override-ctor seam the B4 tests rely on, the resolver's defaulting (a malformed/missing
/// discovery file falls back to the well-known port as a pid-less Default endpoint), and the loopback
/// guard that refuses a tampered, off-box baseUrl from the world-writable discovery file.
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

    [Theory]
    [InlineData("http://127.0.0.1:8757")]
    [InlineData("http://localhost:8757")]
    [InlineData("https://127.0.0.1:8757")]
    public void TryParseEndpoint_accepts_loopback_http(string url)
    {
        var json = $$"""{ "baseUrl": "{{url}}", "pid": 17 }""";

        Assert.True(EndpointResolver.TryParseEndpoint(json, out var endpoint));
        Assert.Equal(EndpointSource.FromFile, endpoint.Source);
        Assert.Equal(17, endpoint.Pid);
    }

    [Theory]
    [InlineData("http://10.0.0.5:8757")]            // off-box LAN address
    [InlineData("http://attacker.example.com:8757")] // off-box host
    [InlineData("https://example.com")]
    [InlineData("file:///C:/Windows/System32")]     // non-http scheme
    [InlineData("ftp://127.0.0.1")]                  // loopback but wrong scheme
    public void TryParseEndpoint_rejects_non_loopback_or_non_http(string url)
    {
        var json = $$"""{ "baseUrl": "{{url}}", "pid": 17 }""";

        Assert.False(EndpointResolver.TryParseEndpoint(json, out _));
    }

    [Fact]
    public void TryParseEndpoint_rejects_json_without_base_url()
    {
        Assert.False(EndpointResolver.TryParseEndpoint("{}", out _));
    }

    [Fact]
    public void Resolve_rejects_a_tampered_off_box_file_and_falls_back_to_default()
    {
        // End-to-end wiring + the security fix: a hostile endpoint.json must be ignored, not followed.
        // Skip rather than clobber a live desktop's discovery file.
        if (File.Exists(EndpointResolver.FilePath))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(EndpointResolver.FilePath)!);
        File.WriteAllText(EndpointResolver.FilePath,
            """{ "baseUrl": "http://attacker.example.com:8757", "pid": 4242 }""");
        try
        {
            var resolved = new EndpointResolver().ResolveEndpoint();

            Assert.Equal(EndpointSource.Default, resolved.Source);
            Assert.Equal("http://127.0.0.1:8757/", resolved.BaseUrl.ToString());
        }
        finally
        {
            File.Delete(EndpointResolver.FilePath);
        }
    }
}
