namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>Delivery status of an outbox event.</summary>
public enum OutboxEventStatus
{
    /// <summary>Waiting for (another) delivery attempt at <see cref="OutboxEvent.NextAttemptAt"/>.</summary>
    Pending,

    /// <summary>The merchant acknowledged it with a 2xx response.</summary>
    Delivered,

    /// <summary>The retry limit was reached (FR-14); can be resent manually (FR-15).</summary>
    Failed,
}

/// <summary>
/// A webhook event in the transactional outbox (NFR-2). Written in the same transaction as the invoice change
/// that caused it; delivered later by the dispatcher. <see cref="Id"/> (identity) gives the causal order,
/// <see cref="EventId"/> is the public, stable event ID (FR-15, FR-16).
/// </summary>
public sealed class OutboxEvent
{
    private readonly List<WebhookDeliveryAttempt> _deliveryAttempts = [];

    /// <summary>Creates a pending event, due immediately.</summary>
    public OutboxEvent(Guid eventId, Guid invoiceId, string type, string payload, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        EventId = eventId;
        InvoiceId = invoiceId;
        Type = type;
        Payload = payload;
        CreatedAt = createdAt;
        NextAttemptAt = createdAt;
        Status = OutboxEventStatus.Pending;
    }

#pragma warning disable CS8618 // Persistence constructor.
    private OutboxEvent()
#pragma warning restore CS8618
    {
    }

    /// <summary>Insertion order (identity); dispatch in this order to keep events causal.</summary>
    public long Id { get; private set; }

    /// <summary>Public event ID (UUIDv7), kept on resend.</summary>
    public Guid EventId { get; private set; }

    /// <summary>The invoice the event is about.</summary>
    public Guid InvoiceId { get; private set; }

    /// <summary>Event type, e.g. <c>invoice.paid</c>.</summary>
    public string Type { get; private set; }

    /// <summary>The exact JSON body that is sent (and signed).</summary>
    public string Payload { get; private set; }

    /// <summary>Delivery status.</summary>
    public OutboxEventStatus Status { get; private set; }

    /// <summary>Delivery attempts in the current retry cycle (reset by a manual resend).</summary>
    public int Attempts { get; private set; }

    /// <summary>When the event was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the next attempt is due (meaningful while <see cref="OutboxEventStatus.Pending"/>).</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>When it was delivered.</summary>
    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>Error of the last failed attempt.</summary>
    public string? LastError { get; private set; }

    /// <summary>Every delivery attempt, across resends (FR-19 webhook history).</summary>
    public IReadOnlyList<WebhookDeliveryAttempt> DeliveryAttempts => _deliveryAttempts;

    /// <summary>Records a successful delivery.</summary>
    public void RecordSuccess(DateTimeOffset at, int httpStatus, TimeSpan duration)
    {
        Attempts++;
        _deliveryAttempts.Add(new WebhookDeliveryAttempt(at, httpStatus, null, duration));
        Status = OutboxEventStatus.Delivered;
        DeliveredAt = at;
        LastError = null;
    }

    /// <summary>
    /// Records a failed attempt. <paramref name="nextAttemptAt"/> = <c>null</c> means the retry limit is reached
    /// and the event is marked <see cref="OutboxEventStatus.Failed"/>.
    /// </summary>
    public void RecordFailure(DateTimeOffset at, int? httpStatus, string error, TimeSpan duration, DateTimeOffset? nextAttemptAt)
    {
        Attempts++;
        _deliveryAttempts.Add(new WebhookDeliveryAttempt(at, httpStatus, error, duration));
        LastError = error;
        if (nextAttemptAt is { } next)
        {
            NextAttemptAt = next;
        }
        else
        {
            Status = OutboxEventStatus.Failed;
        }
    }

    /// <summary>Manual resend (FR-15): same event ID and payload, a fresh retry budget, due now.</summary>
    public void Resend(DateTimeOffset now)
    {
        Status = OutboxEventStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        DeliveredAt = null;
    }
}

/// <summary>One HTTP delivery attempt of an outbox event.</summary>
public sealed class WebhookDeliveryAttempt
{
    internal WebhookDeliveryAttempt(DateTimeOffset attemptedAt, int? httpStatus, string? error, TimeSpan duration)
    {
        AttemptedAt = attemptedAt;
        HttpStatus = httpStatus;
        Error = error;
        DurationMs = (long)duration.TotalMilliseconds;
    }

    private WebhookDeliveryAttempt()
    {
    }

    /// <summary>Surrogate key (insertion order).</summary>
    public long Id { get; private set; }

    /// <summary>When the request was sent.</summary>
    public DateTimeOffset AttemptedAt { get; private set; }

    /// <summary>HTTP status, if a response was received.</summary>
    public int? HttpStatus { get; private set; }

    /// <summary>Error, if the attempt failed.</summary>
    public string? Error { get; private set; }

    /// <summary>Request duration in milliseconds.</summary>
    public long DurationMs { get; private set; }
}
