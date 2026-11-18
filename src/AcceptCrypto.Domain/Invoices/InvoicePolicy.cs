using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Invoices;

/// <summary>Configurable limits for new invoices (FR-1, FR-3). Bound from configuration by the service.</summary>
public sealed record InvoicePolicy
{
    /// <summary>Smallest allowed invoice amount (inclusive). Default 0.01 USDC.</summary>
    public UsdcAmount MinAmount { get; init; } = UsdcAmount.Parse("0.01");

    /// <summary>Largest allowed invoice amount (inclusive). Default 1,000,000 USDC.</summary>
    public UsdcAmount MaxAmount { get; init; } = UsdcAmount.Parse("1000000");

    /// <summary>Lifetime when the request does not override it. Default 30 minutes.</summary>
    public TimeSpan DefaultLifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Shortest allowed per-invoice lifetime override (inclusive). Default 1 minute.</summary>
    public TimeSpan MinLifetime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Longest allowed per-invoice lifetime override (inclusive). Default 7 days.</summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Max length of the merchant's external order ID.</summary>
    public int MaxExternalOrderIdLength { get; init; } = 200;

    /// <summary>Max length of an idempotency key.</summary>
    public int MaxIdempotencyKeyLength { get; init; } = 255;

    /// <summary>Max number of metadata entries.</summary>
    public int MaxMetadataEntries { get; init; } = 20;

    /// <summary>Max length of a metadata key.</summary>
    public int MaxMetadataKeyLength { get; init; } = 40;

    /// <summary>Max length of a metadata value.</summary>
    public int MaxMetadataValueLength { get; init; } = 500;

    /// <summary>Throws if the policy itself is inconsistent (checked at startup).</summary>
    /// <exception cref="InvalidOperationException">Inconsistent limits.</exception>
    public void EnsureValid()
    {
        if (MinAmount.IsZero || MinAmount > MaxAmount)
        {
            throw new InvalidOperationException("Invoice policy: require 0 < MinAmount <= MaxAmount.");
        }

        if (MinLifetime <= TimeSpan.Zero || MinLifetime > MaxLifetime || DefaultLifetime < MinLifetime || DefaultLifetime > MaxLifetime)
        {
            throw new InvalidOperationException("Invoice policy: require 0 < MinLifetime <= DefaultLifetime <= MaxLifetime.");
        }
    }
}
