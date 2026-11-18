using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AcceptCrypto.IntegrationTests.Webhooks;

/// <summary>A webhook received by <see cref="WebhookReceiver"/>.</summary>
internal sealed record ReceivedWebhook(string EventId, string EventType, string Timestamp, string Signature, string ContentType, string UserAgent, string Body);

/// <summary>
/// A real HTTP merchant endpoint on 127.0.0.1 (Kestrel) that can be stopped and started again on the same port,
/// to simulate the merchant being down (S5). Records every request it answered.
/// </summary>
internal sealed class WebhookReceiver : IAsyncDisposable
{
    private WebApplication? _app;

    private WebhookReceiver(int port) => Port = port;

    public int Port { get; }

    public Uri Url => new($"http://127.0.0.1:{Port}/webhooks/acceptcrypto");

    /// <summary>Status code to answer with.</summary>
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Delay before answering (to provoke timeouts).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    /// <summary>A receiver on a free port that is not listening yet ("merchant down").</summary>
    public static WebhookReceiver Create()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new WebhookReceiver(port);
    }

    public static async Task<WebhookReceiver> StartNewAsync()
    {
        var receiver = Create();
        await receiver.StartAsync();
        return receiver;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, Port));
        var app = builder.Build();
        app.MapPost("/webhooks/acceptcrypto", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay);
            }

            Received.Enqueue(new ReceivedWebhook(
                request.Headers["AcceptCrypto-Event-Id"].ToString(),
                request.Headers["AcceptCrypto-Event-Type"].ToString(),
                request.Headers["AcceptCrypto-Timestamp"].ToString(),
                request.Headers["AcceptCrypto-Signature"].ToString(),
                request.ContentType ?? "",
                request.Headers.UserAgent.ToString(),
                body));
            return Results.StatusCode((int)Status);
        });
        await app.StartAsync();
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
