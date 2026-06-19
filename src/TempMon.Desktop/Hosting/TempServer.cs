using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TempMon.Core;

namespace TempMon.Desktop.Hosting;

/// <summary>
/// In-process Kestrel server (resolved decision #2). Binds 127.0.0.1 only, serves the cached
/// snapshot — it reads <see cref="SensorPoller.Latest"/> and never calls Poll, so an HTTP request
/// can never trigger a hardware read (resolved decision #1).
/// </summary>
internal sealed class TempServer : IAsyncDisposable
{
    private readonly SensorPoller _poller;
    private readonly bool _elevated;
    private WebApplication? _app;

    public Uri? BaseUrl { get; private set; }

    public TempServer(SensorPoller poller, bool elevated)
    {
        _poller = poller;
        _elevated = elevated;
    }

    /// <summary>Starts the server, trying <paramref name="preferredPort"/> first and falling back
    /// up the range if it is taken. Returns the bound base URL.</summary>
    public async Task<Uri> StartAsync(int preferredPort = 8757, int attempts = 10)
    {
        int port = FindFreePort(preferredPort, preferredPort + attempts);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();                 // WinExe has no console
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port));    // 127.0.0.1 only — no external surface

        var app = builder.Build();

        app.MapGet("/temps", () =>
            Results.Content(_poller.Latest.ToJson(), "application/json"));

        app.MapGet("/health", () =>
            Results.Json(new { ok = true, elevated = _elevated }));

        await app.StartAsync();

        _app = app;
        BaseUrl = new Uri($"http://127.0.0.1:{port}");
        return BaseUrl;
    }

    /// <summary>Probes for a free loopback port. There is a small TOCTOU window before Kestrel
    /// binds, which is acceptable for a localhost-only desktop tool.</summary>
    private static int FindFreePort(int start, int end)
    {
        for (int port = start; port <= end; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // port in use — try the next one
            }
        }
        throw new IOException($"No free port available in {start}-{end}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
