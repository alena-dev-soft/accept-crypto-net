using AcceptCrypto.Infrastructure.Chain;
using AcceptCrypto.Infrastructure.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Merchant;

/// <summary>DI registration of the merchant configuration (FR-0) and the webhook SSRF guard (NFR-6).</summary>
public static class MerchantServiceCollectionExtensions
{
    /// <summary>
    /// Binds <c>Merchant</c> and <c>Webhooks</c>, validates both on start (the service refuses to start with a missing
    /// or weak API key / secret, an invalid xpub or a webhook URL that points to the internal network) and registers
    /// <see cref="WebhookUrlGuard"/>.
    /// </summary>
    public static IServiceCollection AddAcceptCryptoMerchant(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .Validate(o => o.IsValid(out _), "Invalid Webhooks configuration (retry schedule, timeout or AllowedHosts).")
            .ValidateOnStart();
        services.AddOptions<MerchantOptions>()
            .Bind(configuration.GetSection(MerchantOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MerchantOptions>, MerchantOptionsValidator>());
        services.TryAddSingleton<IHostAddressResolver, SystemHostAddressResolver>();
        services.TryAddSingleton<WebhookUrlGuard>();
        return services;
    }
}

/// <summary>Validates <see cref="MerchantOptions"/>. Failure messages name the setting, never its value (NFR-5).</summary>
public sealed class MerchantOptionsValidator(IOptions<WebhookOptions> webhookOptions) : IValidateOptions<MerchantOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MerchantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        CheckSecret(options.ApiKey, "Merchant:ApiKey", required: true, failures);
        CheckSecret(options.WebhookSecret, "Merchant:WebhookSecret", required: true, failures);
        CheckSecret(options.PreviousWebhookSecret, "Merchant:PreviousWebhookSecret", required: false, failures);
        if (!string.IsNullOrEmpty(options.PreviousWebhookSecret)
            && string.Equals(options.PreviousWebhookSecret, options.WebhookSecret, StringComparison.Ordinal))
        {
            failures.Add("Merchant:PreviousWebhookSecret must differ from Merchant:WebhookSecret.");
        }

        if (options.WebhookUrl is null)
        {
            failures.Add("Merchant:WebhookUrl is required.");
        }
        else if (WebhookUrlGuard.CheckSyntax(options.WebhookUrl, webhookOptions.Value) is { } urlError)
        {
            failures.Add("Merchant:WebhookUrl " + urlError + ".");
        }

        if (string.IsNullOrWhiteSpace(options.Xpub))
        {
            failures.Add("Merchant:Xpub is required (extended public key at m/44'/60'/0'/0).");
        }
        else
        {
            try
            {
                _ = new XpubDepositAddressDeriver(options.Xpub).DeriveAddress(0);
            }
            catch (ArgumentException)
            {
                // The deriver's message never echoes the key; still, keep ours generic.
                failures.Add("Merchant:Xpub is not a valid extended public key (xpub/tpub); private keys are refused.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckSecret(string? value, string setting, bool required, List<string> failures)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required)
            {
                failures.Add($"{setting} is required.");
            }

            return;
        }

        if (value.Length < MerchantOptions.MinSecretLength || value.Any(char.IsWhiteSpace))
        {
            failures.Add($"{setting} must be at least {MerchantOptions.MinSecretLength} characters without whitespace (e.g. `openssl rand -hex 32`).");
        }
    }
}
