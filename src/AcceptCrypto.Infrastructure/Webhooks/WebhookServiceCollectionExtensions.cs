using AcceptCrypto.Infrastructure.Merchant;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>DI registration of webhook delivery.</summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the merchant configuration (see <see cref="MerchantServiceCollectionExtensions.AddAcceptCryptoMerchant"/>),
    /// <see cref="WebhookDispatcher"/>, <see cref="WebhookMetrics"/> and the SSRF-guarded HTTP client; starts
    /// <see cref="WebhookDispatcherService"/> unless <c>Webhooks:DispatcherEnabled</c> is false. Requires persistence.
    /// </summary>
    public static IServiceCollection AddAcceptCryptoWebhooks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddAcceptCryptoMerchant(configuration);
        services.TryAddSingleton<TimeProvider>(StorageTimeProvider.Instance);
        services.AddMetrics();
        services.AddHttpClient(WebhookDispatcher.HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<WebhookUrlGuard>().CreateHttpHandler());
        services.TryAddSingleton<WebhookMetrics>();
        services.TryAddSingleton<WebhookDispatcher>();

        var enabled = configuration.GetSection(WebhookOptions.SectionName).GetValue(nameof(WebhookOptions.DispatcherEnabled), true);
        if (enabled)
        {
            services.AddHostedService<WebhookDispatcherService>();
        }

        return services;
    }
}
