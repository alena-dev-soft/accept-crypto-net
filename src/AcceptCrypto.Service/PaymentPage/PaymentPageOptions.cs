namespace AcceptCrypto.Service.PaymentPage;

/// <summary>Public payment page settings (section <c>PaymentPage</c>, FR-29, FR-30).</summary>
internal sealed class PaymentPageOptions
{
    public const string SectionName = "PaymentPage";

    /// <summary>Name shown to the payer in the page header, e.g. "Demo Shop". Optional.</summary>
    public string? MerchantDisplayName { get; set; }

    /// <summary>How often the page polls the status while a payment is expected or confirming.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Requests per minute and client IP to the public endpoints (page, QR, status JSON).</summary>
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>
    /// Serves sample pages at <c>/pay/sample?status=…</c> without a database (design review, README screenshots).
    /// Development only; keep it off in production.
    /// </summary>
    public bool EnableSample { get; set; }

    public bool IsValid() =>
        PollInterval >= TimeSpan.FromMilliseconds(500) && PollInterval <= TimeSpan.FromMinutes(1)
        && RateLimitPerMinute > 0
        && (MerchantDisplayName is null || MerchantDisplayName.Length <= 80);
}
