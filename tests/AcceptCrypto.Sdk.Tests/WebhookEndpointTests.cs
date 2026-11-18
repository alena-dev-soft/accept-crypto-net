using System.Globalization;
using System.Net;
using System.Text;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AcceptCrypto.Sdk.Tests;

public sealed class WebhookEndpointTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 5, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<AcceptCryptoEvent> _received = [];
    private WebApplication? _app;
    private int _failuresLeft;

    private async Task<HttpClient> StartAsync(Action<AcceptCryptoOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddAcceptCrypto(o =>
        {
            o.WebhookSecret = TestData.Secret;
            configure?.Invoke(o);
        });

        _app = builder.Build();
        _app.MapAcceptCryptoWebhook("/webhooks", e =>
        {
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new InvalidOperationException("merchant database down");
            }

            _received.Add(e);
            return Task.CompletedTask;
        });
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    private static HttpRequestMessage Webhook(string body, DateTimeOffset at, params string[] secrets)
    {
        var timestamp = at.ToUnixTimeSeconds();
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.Add("AcceptCrypto-Event-Id", TestData.EventId.ToString());
        request.Headers.Add("AcceptCrypto-Event-Type", "invoice.paid");
        request.Headers.Add("AcceptCrypto-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("AcceptCrypto-Signature", AcceptCryptoWebhookVerifier.ComputeSignatureHeader(secrets, timestamp, bytes));
        return request;
    }

    [Fact]
    public async Task AValidWebhookReachesTheHandlerAsATypedEvent()
    {
        var client = await StartAsync();

        var response = await client.SendAsync(Webhook(TestData.PaidEventJson, Now, TestData.Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paid = Assert.IsType<InvoiceStatusChangedEvent>(Assert.Single(_received));
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal("order-1001", paid.Invoice.ExternalOrderId);
    }

    [Fact]
    public async Task AForgedWebhookIsRejectedWithoutCallingTheHandler()
    {
        var client = await StartAsync();

        var response = await client.SendAsync(Webhook(TestData.PaidEventJson, Now, "attacker_secret_0000000000000000000000000"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("No valid v1 signature", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task AReplayedWebhookOutsideTheToleranceIsRejected()
    {
        var client = await StartAsync();

        var response = await client.SendAsync(Webhook(TestData.PaidEventJson, Now.AddMinutes(-6), TestData.Secret));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("tolerance", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task UnsignedRequestsAreRejected()
    {
        var client = await StartAsync();

        var response = await client.PostAsync("/webhooks", new StringContent(TestData.PaidEventJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task DuringRotationTheOldSecretIsAccepted()
    {
        var client = await StartAsync(o => o.AdditionalWebhookSecrets.Add(TestData.OldSecret));

        var response = await client.SendAsync(Webhook(TestData.PaidEventJson, Now, TestData.OldSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_received);
    }

    [Fact]
    public async Task ADuplicateDeliveryIsAcknowledgedButHandledOnce()
    {
        var client = await StartAsync();

        var first = await client.SendAsync(Webhook(TestData.PaidEventJson, Now, TestData.Secret));
        _clock.Advance(TimeSpan.FromSeconds(30));
        var retry = await client.SendAsync(Webhook(TestData.PaidEventJson, _clock.GetUtcNow(), TestData.Secret));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Single(_received);
    }

    [Fact]
    public async Task AFailingHandlerAnswers500AndTheRetryIsProcessed()
    {
        var client = await StartAsync();
        _failuresLeft = 1;

        var failed = await client.SendAsync(Webhook(TestData.PaidEventJson, Now, TestData.Secret));
        var retried = await client.SendAsync(Webhook(TestData.PaidEventJson, Now, TestData.Secret));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("merchant database down", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Single(_received);
    }

    [Fact]
    public async Task ATooLargeBodyIsRejected()
    {
        var client = await StartAsync(o => o.WebhookMaxBodyBytes = 100);

        var response = await client.SendAsync(Webhook(TestData.PaidEventJson, Now, TestData.Secret));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task AHandlerClassCanBeMapped()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<RecordingHandler>();
        builder.Services.AddAcceptCrypto(o => o.WebhookSecret = TestData.Secret);
        _app = builder.Build();
        _app.MapAcceptCryptoWebhook<RecordingHandler>("/webhooks");
        await _app.StartAsync();

        var response = await _app.GetTestClient().SendAsync(Webhook(TestData.RevertedEventJson, Now, TestData.Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<PaymentRevertedEvent>(Assert.Single(_app.Services.GetRequiredService<RecordingHandler>().Events));
    }

    [Fact]
    public async Task MappingWithoutASecretFailsFast()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAcceptCrypto();
        await using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapAcceptCryptoWebhook("/webhooks", _ => Task.CompletedTask));

        Assert.Contains("WebhookSecret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappingWithoutAddAcceptCryptoFailsFast()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => app.MapAcceptCryptoWebhook("/webhooks", _ => Task.CompletedTask));

        Assert.Contains("AddAcceptCrypto", ex.Message, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class RecordingHandler : IAcceptCryptoWebhookHandler
    {
        public List<AcceptCryptoEvent> Events { get; } = [];

        public Task HandleAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken)
        {
            Events.Add(webhookEvent);
            return Task.CompletedTask;
        }
    }
}
