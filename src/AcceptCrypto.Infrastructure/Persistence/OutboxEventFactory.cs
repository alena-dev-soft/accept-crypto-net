using System.Text.Json;
using System.Text.Json.Serialization;
using AcceptCrypto.Domain.Invoices;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>
/// Turns domain events into outbox rows. The payload is the final webhook body, fixed at enqueue time so a
/// resend (FR-15) sends the same document under the same event ID.
/// </summary>
/// <remarks>
/// Body: <c>{"id","type","created_at","data":{...}}</c>, snake_case, amounts as decimal strings (NFR-1).
/// </remarks>
public static class OutboxEventFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

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

        object data = domainEvent switch
        {
            InvoiceStatusChanged e => new StatusChangedData(
                e.InvoiceId,
                invoice.ExternalOrderId,
                StatusName(e.PreviousStatus),
                StatusName(e.Status),
                e.Amount.ToString(),
                e.ConfirmedTotal.ToString()),
            PaymentReverted e => new PaymentRevertedData(
                e.InvoiceId,
                invoice.ExternalOrderId,
                e.Transfer.TxHash,
                e.Transfer.LogIndex,
                e.TransferAmount.ToString(),
                e.BlockNumber,
                e.BlockHash),
            _ => throw new ArgumentException($"Unknown event type {domainEvent.GetType().Name}.", nameof(domainEvent)),
        };

        var body = new Envelope(domainEvent.EventId, domainEvent.Type, domainEvent.OccurredAt.ToUniversalTime(), data);
        var payload = JsonSerializer.Serialize(body, JsonOptions);
        return new OutboxEvent(domainEvent.EventId, domainEvent.InvoiceId, domainEvent.Type, payload, domainEvent.OccurredAt);
    }

    private sealed record Envelope(Guid Id, string Type, DateTimeOffset CreatedAt, object Data);

    private sealed record StatusChangedData(
        Guid InvoiceId,
        string ExternalOrderId,
        string PreviousStatus,
        string Status,
        string Amount,
        string ConfirmedTotal);

    private sealed record PaymentRevertedData(
        Guid InvoiceId,
        string ExternalOrderId,
        string TxHash,
        int LogIndex,
        string Amount,
        long BlockNumber,
        string BlockHash);
}
