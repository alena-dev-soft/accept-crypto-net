using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Infrastructure.Persistence;

namespace AcceptCrypto.Service.Api;

// Wire format: snake_case JSON, amounts as decimal strings in USDC (NFR-1), timestamps ISO 8601 UTC.

/// <summary>Create an invoice (FR-1). Send an <c>Idempotency-Key</c> header to make retries safe (FR-2).</summary>
internal sealed record CreateInvoiceRequest
{
    /// <summary>Amount in USDC.</summary>
    [Required]
    [Description("Amount in USDC as a decimal string, at most 6 decimals, e.g. \"25.50\".")]
    public string? Amount { get; init; }

    /// <summary>The merchant's order ID.</summary>
    [Required]
    [Description("The merchant's own order ID.")]
    public string? ExternalOrderId { get; init; }

    /// <summary>Merchant metadata.</summary>
    [Description("Optional string key/value pairs, returned to the merchant only (never shown to the payer).")]
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>Lifetime override.</summary>
    [Description("Optional lifetime override in seconds, within the configured limits.")]
    public long? ExpiresInSeconds { get; init; }
}

/// <summary>What the payer needs to pay (FR-4).</summary>
internal sealed record PaymentDetails(
    [property: Description("Network name, e.g. \"Base Sepolia\".")] string Network,
    long ChainId,
    [property: Description("Token symbol, e.g. \"USDC\".")] string Token,
    [property: Description("ERC-20 token contract address.")] string? TokenAddress,
    [property: Description("Token decimals (6 for USDC).")] int Decimals,
    [property: Description("Deposit address unique to this invoice.")] string Address,
    [property: Description("Exact amount to send, decimal string.")] string Amount,
    [property: Description("Still to pay (amount minus confirmed total), decimal string.")] string RemainingAmount,
    DateTimeOffset ExpiresAt);

/// <summary>An invoice as the merchant sees it.</summary>
internal record InvoiceResponse(
    Guid Id,
    InvoiceStatus Status,
    string Amount,
    string Currency,
    string ExternalOrderId,
    IReadOnlyDictionary<string, string> Metadata,
    string ConfirmedTotal,
    string UnconfirmedTotal,
    string RemainingAmount,
    string ExcessAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ExpiredAt,
    DateTimeOffset UpdatedAt,
    [property: Description("Public payment page for the payer (unguessable token).")] Uri PaymentPageUrl,
    PaymentDetails Payment);

/// <summary>Invoice with its transfers and webhook history (FR-19).</summary>
internal sealed record InvoiceDetailsResponse(
    Guid Id,
    InvoiceStatus Status,
    string Amount,
    string Currency,
    string ExternalOrderId,
    IReadOnlyDictionary<string, string> Metadata,
    string ConfirmedTotal,
    string UnconfirmedTotal,
    string RemainingAmount,
    string ExcessAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ExpiredAt,
    DateTimeOffset UpdatedAt,
    Uri PaymentPageUrl,
    PaymentDetails Payment,
    IReadOnlyList<TransferResponse> Transfers,
    IReadOnlyList<EventResponse> Events)
    : InvoiceResponse(Id, Status, Amount, Currency, ExternalOrderId, Metadata, ConfirmedTotal, UnconfirmedTotal, RemainingAmount,
        ExcessAmount, CreatedAt, ExpiresAt, ExpiredAt, UpdatedAt, PaymentPageUrl, Payment);

/// <summary>An on-chain transfer to the invoice's deposit address.</summary>
internal sealed record TransferResponse(
    string TxHash,
    int LogIndex,
    string From,
    string Amount,
    long BlockNumber,
    string BlockHash,
    DateTimeOffset BlockTimestamp,
    [property: Description("Block timestamp before the deadline.")] bool OnTime,
    TransferState State,
    [property: Description("Blocks on top of the transfer's block incl. itself, if the chain head is known.")] long? Confirmations,
    DateTimeOffset SeenAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? RevertedAt);

/// <summary>A webhook event and its delivery history.</summary>
internal sealed record EventResponse(
    Guid Id,
    string Type,
    Guid InvoiceId,
    OutboxEventStatus DeliveryStatus,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? DeliveredAt,
    string? LastError,
    IReadOnlyList<DeliveryAttemptResponse> DeliveryAttempts);

/// <summary>One HTTP delivery attempt.</summary>
internal sealed record DeliveryAttemptResponse(DateTimeOffset AttemptedAt, int? HttpStatus, string? Error, long DurationMs);

/// <summary>A transfer that matched no open invoice (FR-11a).</summary>
internal sealed record UnmatchedPaymentResponse(
    long Id,
    long ChainId,
    string TxHash,
    int LogIndex,
    long BlockNumber,
    string BlockHash,
    DateTimeOffset BlockTimestamp,
    string From,
    string To,
    string Amount,
    UnmatchedReason Reason,
    [property: Description("The closed invoice the address belongs to, if any.")] Guid? InvoiceId,
    DateTimeOffset RecordedAt);

/// <summary>A page of a cursor-paginated list (FR-18).</summary>
internal sealed record ListResponse<T>(
    IReadOnlyList<T> Data,
    [property: Description("Pass as `cursor` to get the next page; null on the last page.")] string? NextCursor);
