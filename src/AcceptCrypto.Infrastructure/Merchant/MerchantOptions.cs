namespace AcceptCrypto.Infrastructure.Merchant;

/// <summary>
/// The single merchant of this deployment (FR-0, section <c>Merchant</c>). Configured, not registered: every value
/// comes from appsettings / environment variables / user-secrets, never from code. The secret values are never
/// logged and never part of validation messages (NFR-5).
/// </summary>
public sealed class MerchantOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Merchant";

    /// <summary>Minimum length of the API key and of webhook secrets.</summary>
    public const int MinSecretLength = 32;

    /// <summary>API key the merchant sends in the <c>X-Api-Key</c> header (FR-17). Secret.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Where webhooks are POSTed. https, or http for a host on <c>Webhooks:AllowedHosts</c> (NFR-6).</summary>
    public Uri? WebhookUrl { get; set; }

    /// <summary>Current webhook signing secret (HMAC-SHA256). Secret.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Previous webhook secret, set only while rotating (FR-0): the merchant accepts signatures made with either
    /// secret until the rotation is finished, then this setting is removed. Secret.
    /// </summary>
    public string? PreviousWebhookSecret { get; set; }

    /// <summary>
    /// Extended public key (xpub/tpub at <c>m/44'/60'/0'/0</c>) the deposit addresses are derived from (D1, D13).
    /// Not a secret, but private keys (xprv) are rejected.
    /// </summary>
    public string? Xpub { get; set; }

    /// <summary>The secrets webhooks can be verified with, current first.</summary>
    public IReadOnlyList<string> WebhookSecrets =>
        new[] { WebhookSecret, PreviousWebhookSecret }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray();

    /// <summary>Redacted: never prints secrets.</summary>
    public override string ToString() =>
        $"MerchantOptions {{ WebhookUrl = {(WebhookUrl is { IsAbsoluteUri: true } u ? u.Scheme + "://" + u.Host : "(none)")}, ApiKey = ***, WebhookSecrets = {WebhookSecrets.Count} configured }}";
}
