using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TempMon.Mcp;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol — every log line must go to stderr or it corrupts the stream.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// A single shared client is plenty for localhost polling; timeout set once so it's never mutated
// mid-flight. Connection-refused (desktop not running) fails fast on its own.
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(5) });
builder.Services.AddSingleton<EndpointResolver>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
