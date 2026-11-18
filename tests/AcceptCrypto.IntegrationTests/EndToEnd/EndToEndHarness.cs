using System.Net;
using System.Net.Sockets;
using AcceptCrypto.IntegrationTests.Api;
using AcceptCrypto.IntegrationTests.Chain;
using AcceptCrypto.IntegrationTests.Persistence;
using AcceptCrypto.LocalChain;
using AcceptCrypto.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace AcceptCrypto.IntegrationTests.EndToEnd;

/// <summary>
/// The whole system for one end-to-end test, wired like the docker compose stack:
/// <list type="bullet">
/// <item>the real service host (<c>Program.cs</c>: merchant API, payment page, chain scanner, expiry worker, webhook
/// dispatcher) on a fresh PostgreSQL database and the shared Anvil chain (reset per test);</item>
/// <item>a merchant app built on the SDK (<see cref="MerchantApp"/>, real Kestrel on 127.0.0.1) receiving the webhooks;</item>
/// <item>the SDK's typed client talking to the service.</item>
/// </list>
/// The service can be stopped and started again on the same database (<see cref="RestartServiceAsync"/>) and the
/// merchant app can be down (<see cref="MerchantApp.StopAsync"/>).
/// </summary>
internal sealed class EndToEndHarness : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    private readonly TestStore _store;
    private readonly Dictionary<string, string?> _overrides;
    private WebApplicationFactory<Program>? _service;

    private EndToEndHarness(AnvilFixture chain, TestStore store, MerchantApp merchant, Dictionary<string, string?> overrides)
    {
        Chain = chain;
        _store = store;
        Merchant = merchant;
        _overrides = overrides;
    }

    public AnvilFixture Chain { get; }

    public MerchantApp Merchant { get; }

    /// <summary>SDK client of the running service instance (a new one after a restart).</summary>
    public AcceptCryptoClient Client { get; private set; } = null!;

    /// <summary>Plain HTTP client of the running service (payment page, health, metrics).</summary>
    public HttpClient Http { get; private set; } = null!;

    public static async Task<EndToEndHarness> StartAsync(
        AnvilFixture chain,
        PostgresFixture postgres,
        Dictionary<string, string?>? overrides = null,
        bool merchantUp = true)
    {
        await chain.ResetAsync();
        // After anvil_reset the chain clock restarts at the process's genesis time; block timestamps must follow
        // the wall clock here because the service's deadlines do (S4: on time vs late is decided by block time).
        await chain.Anvil.Client.SendRequestAsync<object?>("evm_setTime", null, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await chain.Anvil.MineAsync();
        var store = await postgres.CreateStoreAsync();
        var merchant = MerchantApp.Create();
        if (merchantUp)
        {
            await merchant.StartAsync();
        }

        var harness = new EndToEndHarness(chain, store, merchant, overrides ?? []);
        harness.StartService();
        return harness;
    }

    /// <summary>Service settings: fast polling and short retries, the merchant app allowlisted (as the demo shop is).</summary>
    private Dictionary<string, string?> Settings()
    {
        var settings = TestMerchant.Settings();
        foreach (var (key, value) in new Dictionary<string, string?>
        {
            ["ConnectionStrings:AcceptCrypto"] = _store.ConnectionString,
            ["Merchant:WebhookUrl"] = Merchant.WebhookUrl.ToString(),
            ["Webhooks:DispatcherEnabled"] = "true",
            ["Webhooks:AllowedHosts:0"] = "127.0.0.1",
            ["Webhooks:PollInterval"] = "00:00:00.100",
            ["Webhooks:InitialRetryDelay"] = "00:00:00.250",
            ["Webhooks:MaxRetryDelay"] = "00:00:01",
            ["Webhooks:RequestTimeout"] = "00:00:05",
            ["Webhooks:ClaimLease"] = "00:00:10",
            ["Webhooks:MaxAttempts"] = "1000", // fast retries must not exhaust the limit while the merchant is down (S5)
            ["Chain:Enabled"] = "true",
            ["Chain:RpcUrl"] = Chain.RpcUrl,
            ["Chain:TokenAddress"] = AnvilAccounts.MockUsdcAddress,
            ["Chain:Confirmations"] = "2",
            ["Chain:StartBlock"] = "0",
            ["Chain:PollInterval"] = "00:00:00.150",
            ["Chain:ExpiryInterval"] = "00:00:00.150",
            ["Chain:RetryBaseDelay"] = "00:00:00.200",
            ["Chain:RetryMaxDelay"] = "00:00:01",
            ["Invoices:MinLifetime"] = "00:00:02",
            ["Observability:MetricsEnabled"] = "true",
        })
        {
            settings[key] = value;
        }

        foreach (var (key, value) in _overrides)
        {
            settings[key] = value;
        }

        return settings;
    }

    private void StartService()
    {
        var settings = Settings();
        _service = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        });
        Http = _service.CreateClient(); // starts the host and its background services
        Client = new AcceptCryptoClient(_service.CreateClient(), new AcceptCryptoOptions
        {
            BaseUrl = _service.Server.BaseAddress,
            ApiKey = TestMerchant.ApiKey,
        });
    }

    /// <summary>Stops the service (all background workers) and leaves it down until <see cref="StartServiceAgainAsync"/>.</summary>
    public async Task StopServiceAsync()
    {
        Http.Dispose();
        if (_service is not null)
        {
            await _service.DisposeAsync();
            _service = null;
        }
    }

    public Task StartServiceAgainAsync()
    {
        StartService();
        return Task.CompletedTask;
    }

    public async Task RestartServiceAsync()
    {
        await StopServiceAsync();
        await StartServiceAgainAsync();
    }

    public async Task<CreatedInvoice> CreateInvoiceAsync(string amount, string? orderId = null, TimeSpan? expiresIn = null) =>
        await Client.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            Amount = UsdcAmount.Parse(amount),
            ExternalOrderId = orderId ?? "order-" + Guid.NewGuid().ToString("N")[..8],
            Metadata = new Dictionary<string, string> { ["customer"] = "c-42" },
            ExpiresIn = expiresIn,
        });

    /// <summary>The scripted payer sends USDC to the invoice's deposit address (mined at once: 1 confirmation).</summary>
    public async Task<string> PayAsync(Invoice invoice, string amount)
    {
        var receipt = await Chain.Usdc.TransferAsync(invoice.Payment.Address, Domain.Money.UsdcAmount.Parse(amount));
        return receipt.TransactionHash;
    }

    public Task MineAsync(int blocks = 1) => Chain.Anvil.MineAsync(blocks);

    /// <summary>Polls the API until the invoice has <paramref name="status"/>; mines a block per round if asked (to reach N).</summary>
    public async Task<InvoiceDetails> WaitForStatusAsync(Guid invoiceId, InvoiceStatus status, bool mine = false)
    {
        InvoiceDetails? last = null;
        await Eventually.UntilAsync(async () =>
        {
            last = await Client.GetInvoiceAsync(invoiceId);
            if (last.Status != status && mine)
            {
                await MineAsync();
            }

            return last.Status == status;
        }, () => $"invoice {invoiceId}: expected {status}, last seen {last?.Status}");
        return last!;
    }

    /// <summary>Waits until the merchant app has handled (verified + parsed) an event of this type for the invoice.</summary>
    public Task<AcceptCrypto.Sdk.Webhooks.AcceptCryptoEvent> WaitForWebhookAsync(Guid invoiceId, string type) =>
        Eventually.ValueAsync(
            () => Task.FromResult(Merchant.Handled.FirstOrDefault(e => e.Invoice.Id == invoiceId && e.Type == type)),
            () => $"webhook {type} for {invoiceId}; merchant got [{string.Join(", ", Merchant.TypesFor(invoiceId))}]");

    /// <summary>Waits until every event of the invoice is delivered according to the service (outbox drained).</summary>
    public async Task<InvoiceDetails> WaitUntilAllDeliveredAsync(Guid invoiceId)
    {
        InvoiceDetails? last = null;
        await Eventually.UntilAsync(async () =>
        {
            last = await Client.GetInvoiceAsync(invoiceId);
            return last.Events.Count > 0 && last.Events.All(e => e.DeliveryStatus == EventDeliveryStatus.Delivered);
        }, () => $"events of {invoiceId} not all delivered: {string.Join(", ", last?.Events.Select(e => $"{e.Type}:{e.DeliveryStatus}") ?? [])}");
        return last!;
    }

    public async ValueTask DisposeAsync()
    {
        await StopServiceAsync();
        await Merchant.DisposeAsync();
        await _store.DisposeAsync();
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Polling with a deadline; the failure message says what was last seen.</summary>
internal static class Eventually
{
    public static async Task UntilAsync(Func<Task<bool>> condition, Func<string> describe, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? EndToEndHarness.DefaultTimeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + describe());
            }

            await Task.Delay(100);
        }
    }

    public static async Task<T> ValueAsync<T>(Func<Task<T?>> probe, Func<string> describe, TimeSpan? timeout = null)
        where T : class
    {
        T? value = null;
        await UntilAsync(async () => (value = await probe()) is not null, describe, timeout);
        return value!;
    }
}
