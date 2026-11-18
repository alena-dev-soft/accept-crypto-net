using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AcceptCrypto.IntegrationTests.Api;

/// <summary>Merchant settings for tests (throwaway values; the xpub is the public test mnemonic's).</summary>
internal static class TestMerchant
{
    public const string ApiKey = "test_api_key_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string WebhookSecret = "test_webhook_secret_bbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string PreviousWebhookSecret = "test_previous_secret_ccccccccccccccccccccccc";
    public const string Xpub = "xpub6EF8jXqFeFEW5bwMU7RpQtHkzE4KJxcqJtvkCjJumzW8CPpacXkb92ek4WzLQXjL93HycJwTPUAcuNxCqFPKKU5m5Z2Vq4nCyh5CyPeBFFr";

    public static Dictionary<string, string?> Settings() => new()
    {
        ["Merchant:ApiKey"] = ApiKey,
        ["Merchant:WebhookSecret"] = WebhookSecret,
        ["Merchant:PreviousWebhookSecret"] = PreviousWebhookSecret,
        ["Merchant:WebhookUrl"] = "https://merchant.example/webhooks",
        ["Merchant:Xpub"] = Xpub,
        ["Webhooks:DispatcherEnabled"] = "false", // the API tests drive the outbox themselves
    };
}

/// <summary>Collects every log message of the host (to prove secrets are never logged).</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>The real service host on a fresh migrated database, a fake clock and an authenticated client.</summary>
public sealed class ApiHarness : IAsyncDisposable
{
    private readonly TestStore _store;

    private ApiHarness(TestStore store, WebApplicationFactory<Program> factory, FakeTimeProvider clock, CapturingLoggerProvider logs)
    {
        _store = store;
        Factory = factory;
        Clock = clock;
        Logs = logs;
        Client = factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Api-Key", TestMerchant.ApiKey);
        Anonymous = factory.CreateClient();
    }

    public static DateTimeOffset Start { get; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public WebApplicationFactory<Program> Factory { get; }

    public FakeTimeProvider Clock { get; }

    internal CapturingLoggerProvider Logs { get; }

    public HttpClient Client { get; }

    public HttpClient Anonymous { get; }

    public IInvoiceRepository Invoices => Factory.Services.GetRequiredService<IInvoiceRepository>();

    public IOutboxRepository Outbox => Factory.Services.GetRequiredService<IOutboxRepository>();

    public IUnmatchedPaymentRepository Unmatched => Factory.Services.GetRequiredService<IUnmatchedPaymentRepository>();

    public static async Task<ApiHarness> CreateAsync(PostgresFixture postgres, Dictionary<string, string?>? overrides = null)
    {
        var store = await postgres.CreateStoreAsync();
        var clock = new FakeTimeProvider(Start) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
        var logs = new CapturingLoggerProvider();
        var settings = TestMerchant.Settings();
        settings["ConnectionStrings:AcceptCrypto"] = store.ConnectionString;
        settings["Api:PublicBaseUrl"] = "https://pay.example";
        foreach (var (k, v) in overrides ?? [])
        {
            settings[k] = v;
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock));
        });
        return new ApiHarness(store, factory, clock, logs);
    }

    public Task<HttpResponseMessage> CreateInvoiceAsync(object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/invoices") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return Client.SendAsync(request);
    }

    public async Task<JsonObject> CreateInvoiceJsonAsync(string amount = "10", string? orderId = null)
    {
        using var response = await CreateInvoiceAsync(new { amount, external_order_id = orderId ?? "order-" + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    public static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response) =>
        (JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Empty body")).AsObject();

    /// <summary>Asserts an RFC 9457 Problem Details response and returns it.</summary>
    public static async Task<JsonObject> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadJsonAsync(response);
        Assert.Equal((int)status, problem["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(problem["title"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(problem["type"]?.GetValue<string>()));
        Assert.Equal(code, problem["code"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(problem["trace_id"]?.GetValue<string>()));
        return problem;
    }

    /// <summary>Records a transfer of <paramref name="amount"/> to the invoice (as the scanner would) and optionally confirms it.</summary>
    public async Task<ChainTransfer> PayAsync(Guid invoiceId, string amount, bool confirm, int n = 1)
    {
        var invoice = await Invoices.GetAsync(invoiceId) ?? throw new InvalidOperationException("No invoice");
        var transfer = new ChainTransfer(
            TestStore.Hash(n), 0, 100 + n, TestStore.Hash(100_000 + n), Clock.GetUtcNow(), TestStore.PayerAddress,
            invoice.DepositAddress.ToLowerInvariant(), UsdcAmount.Parse(amount));
        await Invoices.UpdateAsync(invoiceId, i =>
        {
            var r = i.RecordTransfer(transfer, Clock.GetUtcNow());
            return new InvoiceMutation<TransferRecordOutcome>(r.Outcome, r.Events);
        });
        if (confirm)
        {
            await Invoices.UpdateAsync(invoiceId, i => new InvoiceMutation<bool>(true, i.ConfirmTransfer(transfer.Id, Clock.GetUtcNow())));
        }

        return transfer;
    }

    public static string Json(object value) => JsonSerializer.Serialize(value);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        Anonymous.Dispose();
        await Factory.DisposeAsync();
        await _store.DisposeAsync();
    }
}
