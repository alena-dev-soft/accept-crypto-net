using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the AcceptCrypto.NET SDK with one call (FR-23).</summary>
public static class AcceptCryptoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SDK and binds <see cref="AcceptCryptoOptions"/> from the configuration section <c>AcceptCrypto</c>,
    /// then applies <paramref name="configure"/> (optional).
    /// </summary>
    /// <returns>The typed client's <see cref="IHttpClientBuilder"/>, e.g. to add a resilience handler.</returns>
    public static IHttpClientBuilder AddAcceptCrypto(this IServiceCollection services, Action<AcceptCryptoOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<AcceptCryptoOptions>().BindConfiguration(AcceptCryptoOptions.SectionName);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return AddCore(services);
    }

    /// <summary>Registers the SDK and binds <see cref="AcceptCryptoOptions"/> from <paramref name="configuration"/> (a section).</summary>
    public static IHttpClientBuilder AddAcceptCrypto(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<AcceptCryptoOptions>().Bind(configuration);
        return AddCore(services);
    }

    private static IHttpClientBuilder AddCore(IServiceCollection services)
    {
        services.AddOptions<AcceptCryptoOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AcceptCryptoOptions>, AcceptCryptoOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWebhookEventDeduplicator>(sp => new InMemoryWebhookEventDeduplicator(sp.GetService<TimeProvider>()));
        services.TryAddTransient(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<AcceptCryptoOptions>>().CurrentValue;
            return new AcceptCryptoWebhookVerifier(options.GetWebhookSecrets(), options.WebhookTolerance, sp.GetService<TimeProvider>());
        });

        return services.AddHttpClient<IAcceptCryptoClient, AcceptCryptoClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<AcceptCryptoOptions>>().CurrentValue;
            if (options.BaseUrl is { } baseUrl)
            {
                http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
            }

            http.Timeout = options.Timeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AcceptCrypto.NET-Sdk/{typeof(AcceptCryptoClient).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        });
    }
}
