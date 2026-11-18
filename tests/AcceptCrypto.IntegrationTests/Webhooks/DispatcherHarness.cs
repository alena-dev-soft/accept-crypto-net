using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.Infrastructure.Webhooks;
using AcceptCrypto.IntegrationTests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AcceptCrypto.IntegrationTests.Webhooks;

/// <summary>
/// The real webhook registration (<c>AddAcceptCryptoWebhooks</c>) wired to a test database and its fake clock, with
/// the merchant's webhook URL pointing at a <see cref="WebhookReceiver"/> on 127.0.0.1 (allowlisted, as the demo shop is).
/// </summary>
internal sealed class DispatcherHarness : IAsyncDisposable
{
    public const string Secret = "test_webhook_secret_bbbbbbbbbbbbbbbbbbbbbbbb";
    public const string PreviousSecret = "test_previous_secret_ccccccccccccccccccccccc";

    private readonly ServiceProvider _services;

    public DispatcherHarness(TestStore store, Uri webhookUrl, IDictionary<string, string?>? overrides = null, bool allowlist = true, IHostAddressResolver? resolver = null)
    {
        Store = store;
        var settings = new Dictionary<string, string?>
        {
            ["Merchant:ApiKey"] = "test_api_key_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["Merchant:WebhookSecret"] = Secret,
            ["Merchant:WebhookUrl"] = webhookUrl.ToString(),
            ["Merchant:Xpub"] = "xpub6EF8jXqFeFEW5bwMU7RpQtHkzE4KJxcqJtvkCjJumzW8CPpacXkb92ek4WzLQXjL93HycJwTPUAcuNxCqFPKKU5m5Z2Vq4nCyh5CyPeBFFr",
            ["Webhooks:DispatcherEnabled"] = "false",
            ["Webhooks:InitialRetryDelay"] = "00:00:30",
            ["Webhooks:MaxRetryDelay"] = "00:10:00",
            ["Webhooks:MaxAttempts"] = "5",
            ["Webhooks:RetryWindow"] = "01:00:00",
            ["Webhooks:RequestTimeout"] = "00:00:02",
            ["Webhooks:ClaimLease"] = "00:01:00",
        };
        if (allowlist)
        {
            settings["Webhooks:AllowedHosts:0"] = "127.0.0.1";
        }

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(store.Clock);
        services.AddSingleton(store.Outbox);
        if (resolver is not null)
        {
            services.AddSingleton(resolver);
        }

        services.AddAcceptCryptoWebhooks(configuration);
        _services = services.BuildServiceProvider();
    }

    public TestStore Store { get; }

    public IServiceProvider Services => _services;

    public WebhookDispatcher Dispatcher => _services.GetRequiredService<WebhookDispatcher>();

    public WebhookMetrics Metrics => _services.GetRequiredService<WebhookMetrics>();

    public Task<int> RunOnceAsync() => Dispatcher.RunOnceAsync();

    /// <summary>An invoice whose creation is followed by a payment seen on-chain: one pending <c>invoice.confirming</c> event.</summary>
    public async Task<OutboxEvent> CreateEventAsync(string amount = "10")
    {
        var invoice = await Store.CreateInvoiceAsync(amount);
        await Store.RecordAsync(invoice.Id, Store.Transfer(invoice.DepositAddress, amount));
        return (await Store.Outbox.ListByInvoiceAsync(invoice.Id)).Single();
    }

    public async Task<OutboxEvent> ReloadAsync(Guid eventId, Guid invoiceId) =>
        (await Store.Outbox.ListByInvoiceAsync(invoiceId)).Single(e => e.EventId == eventId);

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}
