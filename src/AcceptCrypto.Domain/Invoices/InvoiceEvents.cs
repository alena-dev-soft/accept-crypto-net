using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Invoices;

/// <summary>Webhook event type names (FR-12).</summary>
public static class InvoiceEventTypes
{
    /// <summary>Back to pending after a reorg removed every unconfirmed payment.</summary>
    public const string Pending = "invoice.pending";

    /// <summary>A payment is seen, waiting for confirmations.</summary>
    public const string Confirming = "invoice.confirming";

    /// <summary>Confirmed total below the amount.</summary>
    public const string Underpaid = "invoice.underpaid";

    /// <summary>Confirmed total equals the amount.</summary>
    public const string Paid = "invoice.paid";

    /// <summary>Confirmed total exceeds the amount.</summary>
    public const string Overpaid = "invoice.overpaid";

    /// <summary>Deadline passed.</summary>
    public const string Expired = "invoice.expired";

    /// <summary>Paid in full after the deadline.</summary>
    public const string PaidLate = "invoice.paid_late";

    /// <summary>Cancelled by the merchant.</summary>
    public const string Cancelled = "invoice.cancelled";

    /// <summary>A seen payment disappeared from the chain (reorg).</summary>
    public const string PaymentReverted = "payment.reverted";

    /// <summary>The event type emitted when an invoice enters <paramref name="status"/>.</summary>
    public static string ForStatus(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Pending => Pending,
        InvoiceStatus.Confirming => Confirming,
        InvoiceStatus.Underpaid => Underpaid,
        InvoiceStatus.Paid => Paid,
        InvoiceStatus.Overpaid => Overpaid,
        InvoiceStatus.Expired => Expired,
        InvoiceStatus.PaidLate => PaidLate,
        InvoiceStatus.Cancelled => Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown invoice status."),
    };
}

/// <summary>
/// A domain event produced by an invoice operation. <see cref="EventId"/> is unique (UUIDv7) and stays the same
/// across redeliveries and manual resends (FR-15, FR-16). Events returned by one operation are in causal order.
/// </summary>
/// <param name="EventId">Unique event ID.</param>
/// <param name="Type">Event type, see <see cref="InvoiceEventTypes"/>.</param>
/// <param name="InvoiceId">The invoice the event belongs to.</param>
/// <param name="OccurredAt">When the operation happened.</param>
public abstract record InvoiceDomainEvent(Guid EventId, string Type, Guid InvoiceId, DateTimeOffset OccurredAt);

/// <summary>The invoice moved to another status (FR-12).</summary>
/// <param name="EventId">Unique event ID.</param>
/// <param name="InvoiceId">The invoice.</param>
/// <param name="OccurredAt">When the transition happened.</param>
/// <param name="PreviousStatus">Status before the transition.</param>
/// <param name="Status">Status after the transition.</param>
/// <param name="Amount">Invoice amount.</param>
/// <param name="ConfirmedTotal">Confirmed total at the time of the transition (on-time and late).</param>
public sealed record InvoiceStatusChanged(
    Guid EventId,
    Guid InvoiceId,
    DateTimeOffset OccurredAt,
    InvoiceStatus PreviousStatus,
    InvoiceStatus Status,
    UsdcAmount Amount,
    UsdcAmount ConfirmedTotal)
    : InvoiceDomainEvent(EventId, InvoiceEventTypes.ForStatus(Status), InvoiceId, OccurredAt);

/// <summary>A previously seen (unconfirmed) payment disappeared from the canonical chain (FR-10).</summary>
/// <param name="EventId">Unique event ID.</param>
/// <param name="InvoiceId">The invoice.</param>
/// <param name="OccurredAt">When the reorg was processed.</param>
/// <param name="Transfer">The reverted transfer.</param>
/// <param name="TransferAmount">Its amount.</param>
/// <param name="BlockNumber">Block number it had been included in.</param>
/// <param name="BlockHash">Hash of the orphaned block.</param>
public sealed record PaymentReverted(
    Guid EventId,
    Guid InvoiceId,
    DateTimeOffset OccurredAt,
    TransferId Transfer,
    UsdcAmount TransferAmount,
    long BlockNumber,
    string BlockHash)
    : InvoiceDomainEvent(EventId, InvoiceEventTypes.PaymentReverted, InvoiceId, OccurredAt);
