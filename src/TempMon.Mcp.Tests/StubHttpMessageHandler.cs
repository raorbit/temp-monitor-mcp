using System.Net;
using System.Net.Http;

namespace TempMon.Mcp.Tests;

/// <summary>
/// A canned <see cref="HttpMessageHandler"/> so a test can drive <c>TempMonTools</c> without a live
/// desktop. It records every request it sees — that <see cref="RequestCount"/> is what lets the B4
/// fast-fail tests prove no HTTP GET happened when the writer pid was already dead.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    /// <summary>How many requests reached the transport. Stays 0 when the fast-fail short-circuits.</summary>
    public int RequestCount { get; private set; }

    /// <summary>The most recent request URI, for asserting the tool hit <c>/temps</c>.</summary>
    public Uri? LastRequestUri { get; private set; }

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>Returns <paramref name="body"/> with HTTP 200 for every request.</summary>
    public static StubHttpMessageHandler RespondingWith(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        });

    /// <summary>Throws for every request — simulates connection-refused (desktop not running).</summary>
    public static StubHttpMessageHandler Throwing(Exception ex) =>
        new(_ => throw ex);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequestUri = request.RequestUri;
        return Task.FromResult(_responder(request));
    }

    /// <summary>Builds an <see cref="HttpClient"/> over this handler with the production timeout.</summary>
    public HttpClient ToClient() => new(this) { Timeout = TimeSpan.FromSeconds(5) };
}
