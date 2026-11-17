using System.Globalization;
using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AcceptCrypto.IntegrationTests.Persistence;

/// <summary>
/// One PostgreSQL server for all persistence tests: a Testcontainers container (needs Docker, as in CI), or an
/// existing server from <c>ACCEPTCRYPTO_TEST_POSTGRES</c> (a connection string with rights to create databases).
/// Every test gets its own freshly migrated database.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ExternalServerVariable = "ACCEPTCRYPTO_TEST_POSTGRES";

    private PostgreSqlContainer? _container;

    public string ServerConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            ServerConnectionString = external;
            return;
        }

        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();
        ServerConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Creates and migrates a new database; disposing the store drops it.</summary>
    public async Task<TestStore> CreateStoreAsync(params IInterceptor[] interceptors)
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(ServerConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = name }.ConnectionString;
        var store = new TestStore(this, name, connectionString, interceptors);
        await store.Services.MigrateAcceptCryptoDatabaseAsync();
        return store;
    }

    internal async Task DropDatabaseAsync(string name)
    {
        await using var admin = new NpgsqlConnection(ServerConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>A migrated database with the real DI registration, a fake clock and test data builders.</summary>
public sealed class TestStore : IAsyncDisposable
{
    public const long ChainId = 31337;
    public const string PayerAddress = "0x2222222222222222222222222222222222222222";

    private readonly PostgresFixture _fixture;
    private readonly string _databaseName;
    private readonly ServiceProvider _services;
    private int _txCounter;

    internal TestStore(PostgresFixture fixture, string databaseName, string connectionString, IInterceptor[] interceptors)
    {
        _fixture = fixture;
        _databaseName = databaseName;
        ConnectionString = connectionString;

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddAcceptCryptoPersistence(connectionString);
        services.Configure<PersistenceOptions>(o => o.ChainId = ChainId);
        if (interceptors.Length > 0)
        {
            services.ConfigureDbContext<AcceptCryptoDbContext>(o => o.AddInterceptors(interceptors));
        }

        _services = services.BuildServiceProvider();
    }

    public static DateTimeOffset Start { get; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Clock { get; } = new(Start) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };

    public InvoicePolicy Policy { get; } = new();

    public string ConnectionString { get; }

    public IServiceProvider Services => _services;

    public IInvoiceRepository Invoices => _services.GetRequiredService<IInvoiceRepository>();

    public IUnmatchedPaymentRepository Unmatched => _services.GetRequiredService<IUnmatchedPaymentRepository>();

    public IOutboxRepository Outbox => _services.GetRequiredService<IOutboxRepository>();

    public IScannerCheckpointRepository Checkpoints => _services.GetRequiredService<IScannerCheckpointRepository>();

    public DateTimeOffset Now => Clock.GetUtcNow();

    public static string Address(long index) => "0x" + (index + 1).ToString("x40", CultureInfo.InvariantCulture);

    public static string Hash(int n) => "0x" + n.ToString("x64", CultureInfo.InvariantCulture);

    public AcceptCryptoDbContext CreateDbContext() =>
        _services.GetRequiredService<IDbContextFactory<AcceptCryptoDbContext>>().CreateDbContext();

    /// <summary>The invoice factory the API will use: derivation index → deposit address (step 5 derives it from the xpub).</summary>
    public Func<long, Invoice> Factory(string amount = "10", string? idempotencyKey = null, TimeSpan? lifetime = null) =>
        index => Invoice.Create(
            new NewInvoice(UsdcAmount.Parse(amount), "order-" + index, Address(index), index, IdempotencyKey: idempotencyKey, Lifetime: lifetime),
            Policy,
            Clock);

    public async Task<Invoice> CreateInvoiceAsync(string amount = "10", TimeSpan? lifetime = null)
    {
        var result = await Invoices.CreateAsync(null, "fp", Factory(amount, lifetime: lifetime));
        return result.Invoice;
    }

    /// <summary>A new transfer (unique tx hash) to <paramref name="to"/>, block time = now.</summary>
    public ChainTransfer Transfer(string to, string amount, DateTimeOffset? blockTime = null)
    {
        var n = Interlocked.Increment(ref _txCounter);
        return new ChainTransfer(Hash(n), 0, 100 + n, Hash(100_000 + n), blockTime ?? Now, PayerAddress, to, UsdcAmount.Parse(amount));
    }

    public Task<InvoiceUpdate<TransferRecordOutcome>?> RecordAsync(Guid invoiceId, ChainTransfer transfer) =>
        Invoices.UpdateAsync(invoiceId, invoice =>
        {
            var r = invoice.RecordTransfer(transfer, Now);
            return new InvoiceMutation<TransferRecordOutcome>(r.Outcome, r.Events);
        });

    public Task<InvoiceUpdate<bool>?> ConfirmAsync(Guid invoiceId, TransferId transferId) =>
        Invoices.UpdateAsync(invoiceId, invoice => new InvoiceMutation<bool>(true, invoice.ConfirmTransfer(transferId, Now)));

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task<string?> TextAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _fixture.DropDatabaseAsync(_databaseName);
    }
}
