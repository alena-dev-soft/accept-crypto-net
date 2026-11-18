using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Invoices;

/// <summary>Input for <see cref="Invoice.Create"/>.</summary>
/// <param name="Amount">Amount due (FR-1).</param>
/// <param name="ExternalOrderId">The merchant's order ID.</param>
/// <param name="DepositAddress">Deposit address derived from the merchant's xpub at <paramref name="DerivationIndex"/> (D1).</param>
/// <param name="DerivationIndex">Non-hardened BIP32 child index, 0 .. 2^31-1.</param>
/// <param name="Metadata">Optional merchant metadata (never shown on the payment page).</param>
/// <param name="IdempotencyKey">Optional idempotency key of the create request (FR-2).</param>
/// <param name="Lifetime">Optional lifetime override (FR-3); <c>null</c> = policy default.</param>
public sealed record NewInvoice(
    UsdcAmount Amount,
    string ExternalOrderId,
    string DepositAddress,
    long DerivationIndex,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? IdempotencyKey = null,
    TimeSpan? Lifetime = null);
