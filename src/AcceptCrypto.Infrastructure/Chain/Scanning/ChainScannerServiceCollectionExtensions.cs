using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Chain.Scanning;

/// <summary>DI registration of the chain scanner and the expiry worker.</summary>
public static class ChainScannerServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="ChainOptions"/> (section <c>Chain</c>), registers the JSON-RPC client, <see cref="ChainScanner"/>,
    /// <see cref="InvoiceExpiryProcessor"/> and <see cref="ChainScanState"/>, and makes <c>Chain:ChainId</c> the chain ID
    /// used by persistence. When <c>Chain:Enabled</c> is true, also starts both background services, validates the
    /// options on start and adds the <c>chain-scanner</c> health check. Requires the persistence registration.
    /// </summary>
    public static IServiceCollection AddAcceptCryptoChainScanner(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ChainOptions.SectionName);
        var enabled = section.GetValue<bool>(nameof(ChainOptions.Enabled));

        var optionsBuilder = services.AddOptions<ChainOptions>().Bind(section);
        if (enabled)
        {
            optionsBuilder
                .Validate(o => o.IsValid(out _), "Invalid Chain configuration (RpcUrl, TokenAddress, ChainId, Confirmations, ranges, intervals).")
                .ValidateOnStart();
        }

        // One setting for the chain ID: persistence stamps transfers/checkpoints with Chain:ChainId.
        services.AddOptions<PersistenceOptions>()
            .PostConfigure<IOptions<ChainOptions>>((p, c) => p.ChainId = c.Value.ChainId);

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddHttpClient(JsonRpcChainClient.HttpClientName, (sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<ChainOptions>>().Value;
            http.BaseAddress = o.RpcUrl;
            http.Timeout = o.RpcTimeout;
        });
        services.TryAddSingleton<IChainRpcClient, JsonRpcChainClient>();
        services.TryAddSingleton<ChainScanState>();
        services.TryAddSingleton<ChainScanner>();
        services.TryAddSingleton<InvoiceExpiryProcessor>();
        services.TryAddSingleton<ChainScannerHealthCheck>();

        if (enabled)
        {
            services.AddHostedService<ChainScannerService>();
            services.AddHostedService<InvoiceExpiryService>();
            services.AddHealthChecks().AddCheck<ChainScannerHealthCheck>("chain-scanner", tags: ["chain"]);
        }

        return services;
    }
}
