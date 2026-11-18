using Microsoft.Extensions.Options;

namespace AcceptCrypto.Sdk;

/// <summary>
/// Options for the AcceptCrypto.NET SDK, bound through the standard options pattern
/// (configuration section <c>AcceptCrypto</c> by default).
/// </summary>
public sealed class AcceptCryptoOptions
{
    /// <summary>Configuration section name used by default.</summary>
    public const string SectionName = "AcceptCrypto";

    /// <summary>Base URL of the AcceptCrypto.NET service, e.g. <c>https://pay.example.com</c>. Required for the client.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Merchant API key (sent as <c>X-Api-Key</c>). Required for the client. Keep it in secrets, not in code.</summary>
    public string? ApiKey { get; set; }

    /// <summary>HTTP timeout of one API call (default 30 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When <see cref="IAcceptCryptoClient.CreateInvoiceAsync"/> gets no idempotency key, generate a random one
    /// (default true), so the request is safe to retry on the HTTP level. Pass your own stable key (e.g. your order ID)
    /// to be safe across process restarts too.
    /// </summary>
    public bool GenerateIdempotencyKeys { get; set; } = true;

    /// <summary>The current webhook signing secret (the service's <c>Merchant:WebhookSecret</c>).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Further accepted webhook secrets, e.g. the old one during a rotation (FR-0). A webhook is accepted when any
    /// configured secret matches any <c>v1=</c> signature.
    /// </summary>
    public IList<string> AdditionalWebhookSecrets { get; } = [];

    /// <summary>Allowed difference between the webhook timestamp and the local clock (default 5 minutes, FR-26).</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Largest accepted webhook body in bytes (default 1 MiB).</summary>
    public int WebhookMaxBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>All configured webhook secrets, current first, without blanks and duplicates.</summary>
    public IReadOnlyList<string> GetWebhookSecrets() =>
        new[] { WebhookSecret }.Concat(AdditionalWebhookSecrets)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

/// <summary>Validates <see cref="AcceptCryptoOptions"/> at startup. Messages never contain secret values.</summary>
internal sealed class AcceptCryptoOptionsValidator : IValidateOptions<AcceptCryptoOptions>
{
    public ValidateOptionsResult Validate(string? name, AcceptCryptoOptions options)
    {
        var failures = new List<string>();
        if (options.BaseUrl is { } url && (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)))
        {
            failures.Add("AcceptCrypto:BaseUrl must be an absolute http(s) URL.");
        }

        if (options.ApiKey is { } key && (key.Length == 0 || key.Any(char.IsWhiteSpace)))
        {
            failures.Add("AcceptCrypto:ApiKey must not be empty or contain whitespace.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            failures.Add("AcceptCrypto:Timeout must be positive.");
        }

        if (options.WebhookTolerance <= TimeSpan.Zero || options.WebhookTolerance > TimeSpan.FromHours(1))
        {
            failures.Add("AcceptCrypto:WebhookTolerance must be between 0 and 1 hour.");
        }

        if (options.WebhookMaxBodyBytes <= 0)
        {
            failures.Add("AcceptCrypto:WebhookMaxBodyBytes must be positive.");
        }

        if (options.AdditionalWebhookSecrets.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("AcceptCrypto:AdditionalWebhookSecrets must not contain empty entries.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
