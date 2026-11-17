using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.Configuration;

/// <summary>Invoice limits (section <c>Invoices</c>, FR-1, FR-3). Amounts are decimal strings in USDC.</summary>
internal sealed class InvoiceOptions
{
    public const string SectionName = "Invoices";

    /// <summary>Smallest invoice amount, e.g. "0.01".</summary>
    public string MinAmount { get; set; } = "0.01";

    /// <summary>Largest invoice amount, e.g. "1000000".</summary>
    public string MaxAmount { get; set; } = "1000000";

    /// <summary>Lifetime when the request does not set one.</summary>
    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Shortest lifetime a request may ask for.</summary>
    public TimeSpan MinLifetime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Longest lifetime a request may ask for.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The domain policy; call only on validated options.</summary>
    public InvoicePolicy ToPolicy() => new()
    {
        MinAmount = UsdcAmount.Parse(MinAmount),
        MaxAmount = UsdcAmount.Parse(MaxAmount),
        DefaultLifetime = DefaultLifetime,
        MinLifetime = MinLifetime,
        MaxLifetime = MaxLifetime,
    };
}

/// <summary>Validates <see cref="InvoiceOptions"/> with the domain's own policy rules.</summary>
internal sealed class InvoiceOptionsValidator : IValidateOptions<InvoiceOptions>
{
    public ValidateOptionsResult Validate(string? name, InvoiceOptions options)
    {
        if (!UsdcAmount.TryParse(options.MinAmount, out _) || !UsdcAmount.TryParse(options.MaxAmount, out _))
        {
            return ValidateOptionsResult.Fail("Invoices:MinAmount and Invoices:MaxAmount must be USDC decimal strings with at most 6 decimals.");
        }

        try
        {
            options.ToPolicy().EnsureValid();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail("Invoices: " + ex.Message);
        }
    }
}

/// <summary>Merchant API settings (section <c>Api</c>).</summary>
internal sealed class ApiOptions
{
    public const string SectionName = "Api";

    /// <summary>
    /// Public base URL of this service, used for payment page links (<c>{PublicBaseUrl}/pay/{token}</c>). When not set,
    /// the link is built from the request's scheme and host.
    /// </summary>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>Page size when the request sets no <c>limit</c>.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Largest allowed <c>limit</c>.</summary>
    public int MaxPageSize { get; set; } = 200;

    public bool IsValid() =>
        DefaultPageSize >= 1 && MaxPageSize >= DefaultPageSize
        && (PublicBaseUrl is null || (PublicBaseUrl.IsAbsoluteUri && (PublicBaseUrl.Scheme == Uri.UriSchemeHttp || PublicBaseUrl.Scheme == Uri.UriSchemeHttps)));
}
