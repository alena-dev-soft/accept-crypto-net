namespace AcceptCrypto.Sdk;

/// <summary>
/// Options for the AcceptCrypto.NET SDK, bound through the standard options pattern.
/// </summary>
public sealed class AcceptCryptoOptions
{
    /// <summary>Configuration section name used by default.</summary>
    public const string SectionName = "AcceptCrypto";

    /// <summary>Base URL of the AcceptCrypto.NET service.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Merchant API key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Allowed clock skew for webhook timestamps (default 5 minutes).</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);
}
