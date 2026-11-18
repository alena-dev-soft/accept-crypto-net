using System.ComponentModel;
using System.Globalization;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using Microsoft.Extensions.Options;
using Nethereum.Util;

namespace AcceptCrypto.Service.PaymentPage;

/// <summary>
/// What the payer may see about an invoice (FR-4, FR-29): payment instructions and progress only.
/// Deliberately has no invoice ID, external order ID, metadata, idempotency key or transfer senders —
/// adding a field here makes it public, so the type is the allowlist.
/// </summary>
internal sealed record PublicInvoiceView(
    [property: Description("Invoice status (same values as the merchant API).")] InvoiceStatus Status,
    [property: Description("Network name, e.g. \"Base Sepolia\".")] string Network,
    long ChainId,
    [property: Description("Token symbol, e.g. \"USDC\".")] string Token,
    [property: Description("ERC-20 token contract address.")] string? TokenAddress,
    int Decimals,
    [property: Description("Deposit address unique to this invoice.")] string Address,
    [property: Description("Exact invoice amount, decimal string.")] string Amount,
    [property: Description("Confirmed amount received so far.")] string PaidAmount,
    [property: Description("Amount seen on-chain but not yet confirmed.")] string PendingAmount,
    [property: Description("Amount still to send (amount minus confirmed total).")] string RemainingAmount,
    [property: Description("Confirmed amount above the invoice amount.")] string ExcessAmount,
    DateTimeOffset ExpiresAt,
    [property: Description("Confirmation progress of the least-confirmed unconfirmed payment; null when none is waiting.")] ConfirmationProgress? Confirmations,
    [property: Description("EIP-681 payment link for the remaining amount; null when no payment is expected (paid, expired, cancelled) or the token address is unknown.")] string? PaymentUri,
    [property: Description("True while the payer should (still) send funds: pending or underpaid before the deadline.")] bool AcceptingPayment,
    [property: Description("Server clock, for the deadline countdown.")] DateTimeOffset ServerTime);

/// <summary>Confirmations of a seen payment: current of required (FR-7).</summary>
internal sealed record ConfirmationProgress(
    [property: Description("Blocks on top of the payment incl. its own; null if the chain head is not known yet.")] long? Current,
    int Required);

/// <summary>Builds <see cref="PublicInvoiceView"/> from the invoice, the chain settings and the scanner's head.</summary>
internal sealed class PublicInvoiceMapper(IOptionsMonitor<ChainOptions> chain, ChainScanState scanState, TimeProvider clock)
{
    public PublicInvoiceView ToView(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var c = chain.CurrentValue;
        var now = clock.GetUtcNow();
        // Stored addresses are lowercase; the payer gets the EIP-55 checksum form (wallets verify it on paste).
        var address = AddressUtil.Current.ConvertToChecksumAddress(invoice.DepositAddress);
        var accepting = invoice.Status is InvoiceStatus.Pending or InvoiceStatus.Underpaid && now < invoice.ExpiresAt;

        return new PublicInvoiceView(
            invoice.Status, c.NetworkName, c.ChainId, c.TokenSymbol, c.TokenAddress, UsdcAmount.Decimals, address,
            invoice.Amount.ToString(), invoice.ConfirmedTotal.ToString(), invoice.UnconfirmedTotal.ToString(),
            invoice.RemainingAmount.ToString(), invoice.ExcessAmount.ToString(), invoice.ExpiresAt,
            Confirmations(invoice, c.Confirmations),
            accepting && !invoice.RemainingAmount.IsZero ? Eip681.TokenTransfer(c.TokenAddress, c.ChainId, address, invoice.RemainingAmount) : null,
            accepting, now);
    }

    private ConfirmationProgress? Confirmations(Invoice invoice, int required)
    {
        var seen = invoice.Transfers.Where(t => t.State == TransferState.Seen).ToList();
        if (seen.Count == 0)
        {
            return null;
        }

        long? current = scanState.Head is { } head
            ? Math.Clamp(seen.Min(t => head - t.BlockNumber + 1), 0, required)
            : null;
        return new ConfirmationProgress(current, required);
    }
}

/// <summary>EIP-681 payment requests (<c>ethereum:</c> URIs) for ERC-20 transfers.</summary>
internal static class Eip681
{
    /// <summary>
    /// <c>ethereum:{token}@{chainId}/transfer?address={to}&amp;uint256={baseUnits}</c>; null without a token address.
    /// The amount is in base units (6 decimals), written as a plain integer.
    /// </summary>
    public static string? TokenTransfer(string? tokenAddress, long chainId, string to, UsdcAmount amount)
    {
        if (string.IsNullOrWhiteSpace(tokenAddress))
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"ethereum:{tokenAddress}@{chainId}/transfer?address={to}&uint256={amount.BaseUnits}");
    }
}
