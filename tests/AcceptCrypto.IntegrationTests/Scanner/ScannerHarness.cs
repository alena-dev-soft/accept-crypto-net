using System.Globalization;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.IntegrationTests.Chain;
using AcceptCrypto.IntegrationTests.Persistence;
using AcceptCrypto.LocalChain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AcceptCrypto.IntegrationTests.Scanner;

/// <summary>
/// Real PostgreSQL (fresh database) + real Anvil (reset, mock USDC deployed) + the scanner wired through DI like the
/// service does. Passes are run explicitly for determinism; <see cref="RestartAsync"/> throws the whole service
/// provider away (in-memory state included) and builds a new one on the same database, like a process restart.
/// The service clock is a <see cref="FakeTimeProvider"/> started at the chain's latest block timestamp.
/// </summary>
public sealed class ScannerHarness : IAsyncDisposable
{
    private readonly TestStore _store;
    private readonly Dictionary<string, string?> _config;
    private ServiceProvider _services;
    private long _nextIndex;

    private ScannerHarness(AnvilFixture chain, TestStore store, FakeTimeProvider clock, Dictionary<string, string?> config)
    {
        Chain = chain;
        _store = store;
        Clock = clock;
        _config = config;
        _services = Build();
    }

    public AnvilFixture Chain { get; }

    public FakeTimeProvider Clock { get; }

    public InvoicePolicy Policy { get; } = new();

    public IServiceProvider Services => _services;

    public ChainScanner Scanner => _services.GetRequiredService<ChainScanner>();

    public InvoiceExpiryProcessor Expiry => _services.GetRequiredService<InvoiceExpiryProcessor>();

    public ChainScanState State => _services.GetRequiredService<ChainScanState>();

    public IInvoiceRepository Invoices => _services.GetRequiredService<IInvoiceRepository>();

    public IUnmatchedPaymentRepository Unmatched => _services.GetRequiredService<IUnmatchedPaymentRepository>();

    public IOutboxRepository Outbox => _services.GetRequiredService<IOutboxRepository>();

    public IScannerCheckpointRepository Checkpoints => _services.GetRequiredService<IScannerCheckpointRepository>();

    public static async Task<ScannerHarness> CreateAsync(
        PostgresFixture postgres, AnvilFixture chain, int confirmations = 3, int maxBlockRange = 500, int reorgRollbackDepth = 8)
    {
        await chain.ResetAsync();
        var store = await postgres.CreateStoreAsync();
        var latest = await chain.Anvil.GetBlockAsync() ?? throw new InvalidOperationException("No block.");
        var config = new Dictionary<string, string?>
        {
            ["Chain:RpcUrl"] = chain.RpcUrl,
            ["Chain:ChainId"] = AnvilAccounts.ChainId.ToString(CultureInfo.InvariantCulture),
            ["Chain:TokenAddress"] = AnvilAccounts.MockUsdcAddress,
            ["Chain:Confirmations"] = confirmations.ToString(CultureInfo.InvariantCulture),
            ["Chain:MaxBlockRange"] = maxBlockRange.ToString(CultureInfo.InvariantCulture),
            ["Chain:ReorgRollbackDepth"] = reorgRollbackDepth.ToString(CultureInfo.InvariantCulture),
            ["Chain:StartBlock"] = "0",
        };
        return new ScannerHarness(chain, store, new FakeTimeProvider(latest.Timestamp), config);
    }

    /// <summary>Simulates a service restart: new DI container (fresh scanner state), same database and chain.</summary>
    public async Task RestartAsync()
    {
        await _services.DisposeAsync();
        _services = Build();
    }

    public Task<ScanPassResult> PassAsync() => Scanner.RunPassAsync();

    public async Task<Invoice> CreateInvoiceAsync(string amount = "10", TimeSpan? lifetime = null)
    {
        var index = Interlocked.Increment(ref _nextIndex);
        var address = "0x" + (0xD0000000L + index).ToString("x40", CultureInfo.InvariantCulture);
        var result = await Invoices.CreateAsync(
            null,
            "fp",
            i => Invoice.Create(new NewInvoice(UsdcAmount.Parse(amount), "order-" + i, address, i, Lifetime: lifetime), Policy, Clock));
        return result.Invoice;
    }

    public async Task<string> PayAsync(Invoice invoice, string amount) =>
        (await Chain.Usdc.TransferAsync(invoice.DepositAddress, UsdcAmount.Parse(amount))).TransactionHash;

    public Task MineAsync(int blocks = 1) => Chain.Anvil.MineAsync(blocks);

    public async Task<Invoice> ReloadAsync(Guid id) => await Invoices.GetAsync(id) ?? throw new InvalidOperationException("Invoice not found.");

    public async Task<IReadOnlyList<string>> EventTypesAsync(Guid invoiceId) =>
        (await Outbox.ListByInvoiceAsync(invoiceId)).Select(e => e.Type).ToList();

    public Task<long> ObservedTransferRowsAsync(Guid invoiceId) =>
        _store.ScalarAsync($"SELECT count(*) FROM observed_transfers WHERE invoice_id = '{invoiceId}'");

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _store.DisposeAsync();
    }

    private ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(_config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddAcceptCryptoPersistence(_store.ConnectionString);
        services.AddAcceptCryptoChainScanner(configuration);
        return services.BuildServiceProvider();
    }
}
