using System.Text.Json;
using AcceptCrypto.Sdk.Json;

namespace AcceptCrypto.Sdk.Webhooks;

/// <summary>Webhook event types (the <c>type</c> member / <c>AcceptCrypto-Event-Type</c> header).</summary>
public static class AcceptCryptoEventTypes
{
    /// <summary>Back to pending: a reorg removed every unconfirmed payment.</summary>
    public const string InvoicePending = "invoice.pending";

    /// <summary>A payment is seen and waits for confirmations.</summary>
    public const string InvoiceConfirming = "invoice.confirming";

    /// <summary>Confirmed total is below the amount.</summary>
    public const string InvoiceUnderpaid = "invoice.underpaid";

    /// <summary>Paid in full — ship the order.</summary>
    public const string InvoicePaid = "invoice.paid";

    /// <summary>Paid more than the amount.</summary>
    public const string InvoiceOverpaid = "invoice.overpaid";

    /// <summary>The deadline passed (a late payment can still follow).</summary>
    public const string InvoiceExpired = "invoice.expired";

    /// <summary>Paid in full after the deadline.</summary>
    public const string InvoicePaidLate = "invoice.paid_late";

    /// <summary>Cancelled by the merchant.</summary>
    public const string InvoiceCancelled = "invoice.cancelled";

    /// <summary>A seen payment disappeared in a chain reorganization (FR-10).</summary>
    public const string PaymentReverted = "payment.reverted";
}

/// <summary>
/// A verified webhook event (FR-27). Pattern-match on the subtypes <see cref="InvoiceStatusChangedEvent"/> and
/// <see cref="PaymentRevertedEvent"/>; <see cref="UnknownAcceptCryptoEvent"/> stands for types this SDK version
/// does not know yet. Deduplicate by <see cref="Id"/> (FR-16).
/// </summary>
public abstract record AcceptCryptoEvent
{
    /// <summary>Event ID — the same on every retry and manual resend.</summary>
    public Guid Id { get; init; }

    /// <summary>Event type, see <see cref="AcceptCryptoEventTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>When the change happened (not when it was delivered).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Payload schema version.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Snapshot of the invoice as of this event.</summary>
    public required WebhookInvoice Invoice { get; init; }

    /// <summary>The raw JSON body, as signed by the service (for storage or audit).</summary>
    public required string RawJson { get; init; }

    /// <summary>Parses a webhook body. Does <b>not</b> verify the signature — use <see cref="AcceptCryptoWebhookVerifier"/>.</summary>
    /// <exception cref="AcceptCryptoWebhookException">Not a valid event (<see cref="WebhookRejectionReason.MalformedPayload"/>).</exception>
    public static AcceptCryptoEvent Parse(ReadOnlySpan<byte> utf8Json)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(utf8Json, AcceptCryptoJson.Options);
        }
        catch (JsonException ex)
        {
            throw new AcceptCryptoWebhookException(WebhookRejectionReason.MalformedPayload, $"The webhook body is not a valid event: {ex.Message}", ex);
        }

        if (envelope is not { Id: var id, Type: { Length: > 0 } type, ApiVersion: { } apiVersion, Data.Invoice: { } invoice } || id == Guid.Empty)
        {
            throw new AcceptCryptoWebhookException(WebhookRejectionReason.MalformedPayload, "The webhook body lacks id, type, api_version or data.invoice.");
        }

        var raw = System.Text.Encoding.UTF8.GetString(utf8Json);
        var data = envelope.Data;
        if (type == AcceptCryptoEventTypes.PaymentReverted)
        {
            return data.Transfer is { } transfer
                ? new PaymentRevertedEvent { Id = id, Type = type, CreatedAt = envelope.CreatedAt, ApiVersion = apiVersion, Invoice = invoice, RawJson = raw, Transfer = transfer }
                : throw new AcceptCryptoWebhookException(WebhookRejectionReason.MalformedPayload, "payment.reverted without data.transfer.");
        }

        if (type.StartsWith("invoice.", StringComparison.Ordinal) && data.PreviousStatus is { } previous)
        {
            return new InvoiceStatusChangedEvent
            {
                Id = id, Type = type, CreatedAt = envelope.CreatedAt, ApiVersion = apiVersion, Invoice = invoice, RawJson = raw,
                PreviousStatus = previous,
            };
        }

        using var document = JsonDocument.Parse(utf8Json.ToArray());
        return new UnknownAcceptCryptoEvent
        {
            Id = id, Type = type, CreatedAt = envelope.CreatedAt, ApiVersion = apiVersion, Invoice = invoice, RawJson = raw,
            Data = document.RootElement.GetProperty("data").Clone(),
        };
    }

    /// <summary>Parses a webhook body; see <see cref="Parse(ReadOnlySpan{byte})"/>.</summary>
    public static AcceptCryptoEvent Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private sealed record Envelope(Guid Id, string? Type, DateTimeOffset CreatedAt, string? ApiVersion, EnvelopeData? Data);

    private sealed record EnvelopeData(WebhookInvoice? Invoice, InvoiceStatus? PreviousStatus, RevertedTransfer? Transfer);
}

/// <summary>An invoice status change (<c>invoice.*</c>, FR-12).</summary>
public sealed record InvoiceStatusChangedEvent : AcceptCryptoEvent
{
    /// <summary>The new status (= <c>Invoice.Status</c>).</summary>
    public InvoiceStatus Status => Invoice.Status;

    /// <summary>The status before this transition.</summary>
    public InvoiceStatus PreviousStatus { get; init; }
}

/// <summary>A payment that had been seen disappeared in a chain reorganization (<c>payment.reverted</c>, FR-10).</summary>
public sealed record PaymentRevertedEvent : AcceptCryptoEvent
{
    /// <summary>The transfer that is gone.</summary>
    public required RevertedTransfer Transfer { get; init; }
}

/// <summary>An event type this SDK version does not know; <see cref="Data"/> holds the raw <c>data</c> object.</summary>
public sealed record UnknownAcceptCryptoEvent : AcceptCryptoEvent
{
    /// <summary>The <c>data</c> member.</summary>
    public JsonElement Data { get; init; }
}

/// <summary>Invoice snapshot carried by a webhook (no secrets, no payment-page token).</summary>
public sealed record WebhookInvoice
{
    /// <summary>Invoice ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Your order ID.</summary>
    public required string ExternalOrderId { get; init; }

    /// <summary>Status after this event.</summary>
    public InvoiceStatus Status { get; init; }

    /// <summary>Invoice amount.</summary>
    public UsdcAmount Amount { get; init; }

    /// <summary>Confirmed total at this event.</summary>
    public UsdcAmount ConfirmedTotal { get; init; }

    /// <summary>max(amount − confirmed total, 0).</summary>
    public UsdcAmount RemainingAmount { get; init; }

    /// <summary>Deposit address (compare case-insensitively).</summary>
    public required string DepositAddress { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Payment deadline.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Your metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>The transfer a <see cref="PaymentRevertedEvent"/> is about.</summary>
public sealed record RevertedTransfer
{
    /// <summary>Transaction hash.</summary>
    public required string TxHash { get; init; }

    /// <summary>Log index.</summary>
    public int LogIndex { get; init; }

    /// <summary>Amount.</summary>
    public UsdcAmount Amount { get; init; }

    /// <summary>The (now orphaned) block.</summary>
    public long BlockNumber { get; init; }

    /// <summary>The (now orphaned) block hash.</summary>
    public required string BlockHash { get; init; }
}
