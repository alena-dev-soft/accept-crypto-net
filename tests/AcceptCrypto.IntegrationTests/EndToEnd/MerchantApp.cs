using System.Collections.Concurrent;
using System.Net;
using AcceptCrypto.IntegrationTests.Api;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AcceptCrypto.IntegrationTests.EndToEnd;

/// <summary>A raw webhook request as it arrived at the merchant (to replay or tamper with it).</summary>
internal sealed record RawWebhook(string Timestamp, string Signature, string EventId, byte[] Body);

/// <summary>
/// The merchant's ASP.NET Core app, integrated exactly as the SDK README shows: <c>AddAcceptCrypto</c> +
/// <c>MapAcceptCryptoWebhook</c>. It runs on real Kestrel (127.0.0.1, fixed port), so it can be stopped and started
/// again (S5). Handled events, rejections and duplicates are recorded across restarts; the deduplicator is shared
/// across restarts too, as a database-backed one would be. Its clock can be moved forward to replay a webhook later.
/// </summary>
internal sealed class MerchantApp : IAsyncDisposable
{
    private readonly InMemoryWebhookEventDeduplicator _deduplicator;
    private WebApplication? _app;

    private MerchantApp(int port)
    {
        Port = port;
        _deduplicator = new InMemoryWebhookEventDeduplicator(Clock);
    }

    public int Port { get; }

    public Uri BaseUrl => new($"http://127.0.0.1:{Port}");

    public Uri WebhookUrl => new(BaseUrl, "/webhooks/acceptcrypto");

    /// <summary>The merchant's clock (system time + an adjustable offset).</summary>
    public ShiftedTimeProvider Clock { get; } = new();

    /// <summary>Events that passed verification and reached the merchant's handler, in arrival order.</summary>
    public ConcurrentQueue<AcceptCryptoEvent> Handled { get; } = new();

    public ConcurrentQueue<WebhookRejectionReason> Rejected { get; } = new();

    public ConcurrentQueue<Guid> Duplicates { get; } = new();

    /// <summary>Every request to the webhook endpoint as received (before verification).</summary>
    public ConcurrentQueue<RawWebhook> Raw { get; } = new();

    public bool IsRunning => _app is not null;

    public IEnumerable<string> TypesFor(Guid invoiceId) => Handled.Where(e => e.Invoice.Id == invoiceId).Select(e => e.Type);

    /// <summary>A merchant app on a free port that is not listening yet ("merchant down").</summary>
    public static MerchantApp Create() => new(EndToEndHarness.FreePort());

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, Port));
        builder.Services.AddSingleton<TimeProvider>(Clock);
        builder.Services.AddSingleton<IWebhookEventDeduplicator>(_deduplicator);
        builder.Services.AddSingleton<IAcceptCryptoWebhookObserver>(new Observer(this));
        builder.Services.AddAcceptCrypto(o =>
        {
            o.BaseUrl = new Uri("http://service.invalid"); // the client is not used by this app
            o.ApiKey = TestMerchant.ApiKey;
            o.WebhookSecret = TestMerchant.WebhookSecret;
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Request.EnableBuffering();
            using var copy = new MemoryStream();
            await context.Request.Body.CopyToAsync(copy);
            context.Request.Body.Position = 0;
            Raw.Enqueue(new RawWebhook(
                context.Request.Headers["AcceptCrypto-Timestamp"].ToString(),
                context.Request.Headers["AcceptCrypto-Signature"].ToString(),
                context.Request.Headers["AcceptCrypto-Event-Id"].ToString(),
                copy.ToArray()));
            await next(context);
        });
        app.MapAcceptCryptoWebhook("/webhooks/acceptcrypto", e =>
        {
            Handled.Enqueue(e);
            return Task.CompletedTask;
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

    /// <summary>Posts a webhook to the merchant app as an attacker would (any headers, any body).</summary>
    public async Task<HttpStatusCode> PostAsync(string timestamp, string signature, byte[] body)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("AcceptCrypto-Timestamp", timestamp);
        request.Headers.Add("AcceptCrypto-Signature", signature);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private sealed class Observer(MerchantApp owner) : IAcceptCryptoWebhookObserver
    {
        public Task OnRejectedAsync(HttpContext context, WebhookRejectionReason reason, ReadOnlyMemory<byte> body)
        {
            owner.Rejected.Enqueue(reason);
            return Task.CompletedTask;
        }

        public Task OnDuplicateAsync(HttpContext context, AcceptCryptoEvent webhookEvent, WebhookEventClaim claim)
        {
            owner.Duplicates.Enqueue(webhookEvent.Id);
            return Task.CompletedTask;
        }
    }
}

/// <summary>System time plus an offset that a test can move forward.</summary>
internal sealed class ShiftedTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
