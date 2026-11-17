using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>DI registration of the PostgreSQL storage.</summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Connection string name (<c>ConnectionStrings:AcceptCrypto</c>).</summary>
    public const string ConnectionStringName = "AcceptCrypto";

    /// <summary>
    /// Registers the DbContext factory and repositories. The connection string comes from
    /// <c>ConnectionStrings:AcceptCrypto</c> (environment / secrets, never code); nothing connects until first use.
    /// </summary>
    public static IServiceCollection AddAcceptCryptoPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<PersistenceOptions>()
            .Bind(configuration.GetSection(PersistenceOptions.SectionName))
            .Validate(o => o.ChainId > 0 && o.MaxConcurrencyRetries >= 0, "Persistence:ChainId must be positive and MaxConcurrencyRetries non-negative.")
            .ValidateOnStart();
        return services.AddAcceptCryptoPersistence(configuration.GetConnectionString(ConnectionStringName));
    }

    /// <summary>Registers the DbContext factory and repositories with an explicit connection string.</summary>
    public static IServiceCollection AddAcceptCryptoPersistence(this IServiceCollection services, string? connectionString)
    {
        services.AddOptions<PersistenceOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<AcceptCryptoDbContext>(o => ConfigureDbContext(o, connectionString));
        services.AddSingleton<IInvoiceRepository, InvoiceRepository>();
        services.AddSingleton<IUnmatchedPaymentRepository, UnmatchedPaymentRepository>();
        services.AddSingleton<IScannerCheckpointRepository, ScannerCheckpointRepository>();
        services.AddSingleton<IOutboxRepository, OutboxRepository>();
        return services;
    }

    /// <summary>Applies pending EF Core migrations.</summary>
    public static async Task MigrateAcceptCryptoDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AcceptCryptoDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string? connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
