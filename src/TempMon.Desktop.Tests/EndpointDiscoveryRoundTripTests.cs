using System;
using System.IO;
using TempMon.Desktop.Hosting;
using TempMon.Mcp;
using Xunit;

namespace TempMon.Desktop.Tests;

/// <summary>
/// The only discovery contract between the elevated desktop and the un-elevated MCP server is
/// %PROGRAMDATA%\TempMon\endpoint.json. EndpointFile (the writer, in Desktop) and EndpointResolver
/// (the reader, in Mcp) live in separate projects with no shared type, so this round-trip locks the
/// baseUrl/pid field-name agreement they silently depend on.
/// </summary>
public sealed class EndpointDiscoveryRoundTripTests
{
    [Fact]
    public void Written_endpoint_file_resolves_back_to_the_same_endpoint()
    {
        // Skip rather than clobber a live desktop's discovery file.
        if (File.Exists(EndpointResolver.FilePath))
            return;

        var baseUrl = new Uri("http://127.0.0.1:8757");
        EndpointFile.Write(baseUrl, elevated: false);
        try
        {
            var resolved = new EndpointResolver().ResolveEndpoint();

            Assert.Equal(EndpointSource.FromFile, resolved.Source);
            Assert.Equal(baseUrl, resolved.BaseUrl);
            Assert.Equal(Environment.ProcessId, resolved.Pid);
        }
        finally
        {
            EndpointFile.TryDelete();
        }
    }
}
