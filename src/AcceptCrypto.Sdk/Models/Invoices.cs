using System.Text.Json.Serialization;

namespace AcceptCrypto.Sdk;

/// <summary>Request to create an invoice (FR-1).</summary>
public sealed record CreateInvoiceRequest
{
    /// <summary>Amount in USDC (at most 6 decimals, within the service's configured min/max).</summary>
    public required UsdcAmount Amount { get; init; }

    /// <summary>Your own order ID (≤ 200 characters).</summary>
    public required string ExternalOrderId { get; init; }

    /// <summary>Optional key/value pairs returned to you only, never shown to the payer (≤ 20 entries).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Optional lifetime override in whole seconds; default is the service's configured lifetime.</summary>
    public TimeSpan? ExpiresIn { get; init; }
}

/// <summary>What the payer needs to pay (FR-4).</summary>
public sealed record PaymentDetails
{
    /// <summary>Network name, e.g. "Base Sepolia".</summary>
    public required string Network { get; init; }

    /// <summary>EVM chain ID.</summary>
    public long ChainId { get; init; }

    /// <summary>Token symbol ("USDC").</summary>
    public required string Token { get; init; }

    /// <summary>ERC-20 token contract address.</summary>
    public string? TokenAddress { get; init; }

    /// <summary>Token decimals (6).</summary>
    public int Decimals { get; init; }

    /// <summary>Deposit address unique to this invoice.</summary>
    public required string Address { get; init; }

    /// <summary>Exact amount to send.</summary>
    public UsdcAmount Amount { get; init; }

    /// <summary>Still to pay (amount minus confirmed total).</summary>
    public UsdcAmount RemainingAmount { get; init; }

    /// <summary>Payment deadline.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>An invoice as the merchant sees it.</summary>
public record Invoice
{
    /// <summary>Invoice ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Current status.</summary>
    public InvoiceStatus Status { get; init; }

    /// <summary>Invoice amount.</summary>
    public UsdcAmount Amount { get; init; }

    /// <summary>Currency ("USDC").</summary>
    public required string Currency { get; init; }

    /// <summary>Your order ID.</summary>
    public required string ExternalOrderId { get; init; }

    /// <summary>Your metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Sum of confirmed transfers.</summary>
    public UsdcAmount ConfirmedTotal { get; init; }

    /// <summary>Sum of seen, not yet confirmed transfers.</summary>
    public UsdcAmount UnconfirmedTotal { get; init; }

    /// <summary>max(amount − confirmed total, 0).</summary>
    public UsdcAmount RemainingAmount { get; init; }

    /// <summary>max(confirmed total − amount, 0).</summary>
    public UsdcAmount ExcessAmount { get; init; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Payment deadline.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When the invoice moved to expired, if it did.</summary>
    public DateTimeOffset? ExpiredAt { get; init; }

    /// <summary>Last change.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Public payment page for the payer (unguessable link) — redirect your customer here.</summary>
    public required Uri PaymentPageUrl { get; init; }

    /// <summary>Payment instructions (FR-4).</summary>
    public required PaymentDetails Payment { get; init; }
}

/// <summary>The result of <see cref="IAcceptCryptoClient.CreateInvoiceAsync"/>.</summary>
public sealed record CreatedInvoice : Invoice
{
    /// <summary>
    /// True when the service answered with the invoice created earlier for the same idempotency key (FR-2) instead of
    /// creating a new one.
    /// </summary>
    [JsonIgnore]
    public bool IdempotentReplayed { get; init; }
}

/// <summary>An invoice with its transfers and webhook history (FR-19).</summary>
public sealed record InvoiceDetails : Invoice
{
    /// <summary>On-chain transfers to the deposit address.</summary>
    public IReadOnlyList<InvoiceTransfer> Transfers { get; init; } = [];

    /// <summary>Webhook events in causal order, with delivery history.</summary>
    public IReadOnlyList<WebhookEventInfo> Events { get; init; } = [];
}

/// <summary>An on-chain transfer to an invoice's deposit address.</summary>
public sealed record InvoiceTransfer
{
    /// <summary>Transaction hash.</summary>
    public required string TxHash { get; init; }

    /// <summary>Log index of the Transfer event.</summary>
    public int LogIndex { get; init; }

    /// <summary>Sender address.</summary>
    public required string From { get; init; }

    /// <summary>Amount.</summary>
    public UsdcAmount Amount { get; init; }

    /// <summary>Block number.</summary>
    public long BlockNumber { get; init; }

    /// <summary>Block hash.</summary>
    public required string BlockHash { get; init; }

    /// <summary>Block timestamp.</summary>
    public DateTimeOffset BlockTimestamp { get; init; }

    /// <summary>Block timestamp before the deadline.</summary>
    public bool OnTime { get; init; }

    /// <summary>Seen, confirmed or reverted.</summary>
    public TransferState State { get; init; }

    /// <summary>Confirmations, if the chain head is known.</summary>
    public long? Confirmations { get; init; }

    /// <summary>When the service first saw it.</summary>
    public DateTimeOffset SeenAt { get; init; }

    /// <summary>When it reached N confirmations.</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>When it disappeared in a reorg.</summary>
    public DateTimeOffset? RevertedAt { get; init; }
}

/// <summary>A webhook event and its delivery history, as stored by the service.</summary>
public sealed record WebhookEventInfo
{
    /// <summary>Event ID (the same ID the webhook carries).</summary>
    public Guid Id { get; init; }

    /// <summary>Event type, e.g. <c>invoice.paid</c>; see <see cref="Webhooks.AcceptCryptoEventTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>The invoice.</summary>
    public Guid InvoiceId { get; init; }

    /// <summary>Delivery state.</summary>
    public EventDeliveryStatus DeliveryStatus { get; init; }

    /// <summary>Attempts made.</summary>
    public int Attempts { get; init; }

    /// <summary>When the event was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Next attempt, while pending.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>When it was delivered.</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>Last delivery error.</summary>
    public string? LastError { get; init; }

    /// <summary>Every attempt.</summary>
    public IReadOnlyList<WebhookDeliveryAttempt> DeliveryAttempts { get; init; } = [];
}

/// <summary>One HTTP delivery attempt of a webhook.</summary>
public sealed record WebhookDeliveryAttempt
{
    /// <summary>When.</summary>
    public DateTimeOffset AttemptedAt { get; init; }

    /// <summary>HTTP status, null if no response.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>Error text.</summary>
    public string? Error { get; init; }

    /// <summary>Duration in milliseconds.</summary>
    public long DurationMs { get; init; }
}

/// <summary>A transfer that matched no open invoice (FR-11a).</summary>
public sealed record UnmatchedPayment
{
    /// <summary>ID.</summary>
    public long Id { get; init; }

    /// <summary>Chain ID.</summary>
    public long ChainId { get; init; }

    /// <summary>Transaction hash.</summary>
    public required string TxHash { get; init; }

    /// <summary>Log index.</summary>
    public int LogIndex { get; init; }

    /// <summary>Block number.</summary>
    public long BlockNumber { get; init; }

    /// <summary>Block hash.</summary>
    public required string BlockHash { get; init; }

    /// <summary>Block timestamp.</summary>
    public DateTimeOffset BlockTimestamp { get; init; }

    /// <summary>Sender.</summary>
    public required string From { get; init; }

    /// <summary>Receiving address.</summary>
    public required string To { get; init; }

    /// <summary>Amount.</summary>
    public UsdcAmount Amount { get; init; }

    /// <summary>Why it matched nothing.</summary>
    public UnmatchedReason Reason { get; init; }

    /// <summary>The closed invoice the address belongs to, if any.</summary>
    public Guid? InvoiceId { get; init; }

    /// <summary>When it was recorded.</summary>
    public DateTimeOffset RecordedAt { get; init; }
}

/// <summary>One page of a cursor-paginated list (FR-18).</summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed record Page<T>
{
    /// <summary>Items of this page.</summary>
    public IReadOnlyList<T> Data { get; init; } = [];

    /// <summary>Cursor of the next page; null on the last page.</summary>
    public string? NextCursor { get; init; }

    /// <summary>True when another page exists.</summary>
    [JsonIgnore]
    public bool HasMore => NextCursor is not null;
}

/// <summary>Filters and paging for <see cref="IAcceptCryptoClient.ListInvoicesAsync"/>.</summary>
public sealed record ListInvoicesQuery
{
    /// <summary>Only invoices in this status.</summary>
    public InvoiceStatus? Status { get; init; }

    /// <summary>Created at or after (inclusive).</summary>
    public DateTimeOffset? CreatedFrom { get; init; }

    /// <summary>Created before (exclusive).</summary>
    public DateTimeOffset? CreatedTo { get; init; }

    /// <summary>Page size (service default if null).</summary>
    public int? Limit { get; init; }

    /// <summary><see cref="Page{T}.NextCursor"/> of the previous page.</summary>
    public string? Cursor { get; init; }
}

/// <summary>Paging for <see cref="IAcceptCryptoClient.ListUnmatchedPaymentsAsync"/>.</summary>
public sealed record ListUnmatchedPaymentsQuery
{
    /// <summary>Page size (service default if null).</summary>
    public int? Limit { get; init; }

    /// <summary><see cref="Page{T}.NextCursor"/> of the previous page.</summary>
    public string? Cursor { get; init; }
}
