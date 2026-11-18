using System.Text.Json;
using System.Text.Json.Serialization;
using AcceptCrypto.Domain.Invoices;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>
/// Turns domain events into outbox rows. The payload is the final webhook body, fixed at enqueue time so a
/// resend (FR-15) sends the same document under the same event ID.
/// </summary>
/// <remarks>
/// Body: <c>{"id","type","created_at","api_version","data":{"invoice":{...},...}}</c>, snake_case, amounts as
/// decimal strings (NFR-1). The schema is documented in docs/webhooks.md.
/// </remarks>
public static class OutboxEventFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Version of the webhook payload schema (<c>api_version</c>), see docs/webhooks.md.</summary>
    public const string PayloadVersion = "2026-11-17";

    /// <summary>Wire name of a status, e.g. <c>paid_late</c>.</summary>
    public static string StatusName(InvoiceStatus status) => InvoiceEventTypes.ForStatus(status)["invoice.".Length..];

    /// <summary>Creates the outbox row for <paramref name="domainEvent"/> raised by <paramref name="invoice"/>.</summary>
    public static OutboxEvent Create(Invoice invoice, InvoiceDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.InvoiceId != invoice.Id)
        {
            throw new ArgumentException("The event belongs to a different invoice.", nameof(domainEvent));
        }

        // Snapshot of the invoice as of this event. Static fields come from the aggregate; status and totals of a
        // status change come from the event itself (several events can be raised by one operation).
        var (status, confirmedTotal) = domainEvent is InvoiceStatusChanged changed
            ? (changed.Status, changed.ConfirmedTotal)
            : (invoice.Status, invoice.ConfirmedTotal);
        var snapshot = new InvoiceSnapshot(
            invoice.Id,
            invoice.ExternalOrderId,
            StatusName(status),
            invoice.Amount.ToString(),
            confirmedTotal.ToString(),
            invoice.Amount.SubtractOrZero(confirmedTotal).ToString(),
            invoice.DepositAddress,
            invoice.CreatedAt.ToUniversalTime(),
            invoice.ExpiresAt.ToUniversalTime(),
            invoice.Metadata);

        object data = domainEvent switch
        {
            InvoiceStatusChanged e => new StatusChangedData(snapshot, StatusName(e.PreviousStatus)),
            PaymentReverted e => new PaymentRevertedData(
                snapshot,
                new RevertedTransfer(e.Transfer.TxHash, e.Transfer.LogIndex, e.TransferAmount.ToString(), e.BlockNumber, e.BlockHash)),
            _ => throw new ArgumentException($"Unknown event type {domainEvent.GetType().Name}.", nameof(domainEvent)),
        };

        var body = new Envelope(domainEvent.EventId, domainEvent.Type, domainEvent.OccurredAt.ToUniversalTime(), PayloadVersion, data);
        var payload = JsonSerializer.Serialize(body, JsonOptions);
        return new OutboxEvent(domainEvent.EventId, domainEvent.InvoiceId, domainEvent.Type, payload, domainEvent.OccurredAt);
    }

    private sealed record Envelope(Guid Id, string Type, DateTimeOffset CreatedAt, string ApiVersion, object Data);

    private sealed record InvoiceSnapshot(
        Guid Id,
        string ExternalOrderId,
        string Status,
        string Amount,
        string ConfirmedTotal,
        string RemainingAmount,
        string DepositAddress,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        IReadOnlyDictionary<string, string> Metadata);

    private sealed record StatusChangedData(InvoiceSnapshot Invoice, string PreviousStatus);

    private sealed record PaymentRevertedData(InvoiceSnapshot Invoice, RevertedTransfer Transfer);

    private sealed record RevertedTransfer(string TxHash, int LogIndex, string Amount, long BlockNumber, string BlockHash);
}
