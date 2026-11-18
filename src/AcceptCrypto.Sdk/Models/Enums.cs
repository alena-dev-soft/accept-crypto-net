using System.Text.Json.Serialization;
using AcceptCrypto.Sdk.Json;

namespace AcceptCrypto.Sdk;

/// <summary>Invoice status (wire values are snake_case, e.g. <c>paid_late</c>).</summary>
[JsonConverter(typeof(TolerantEnumConverter<InvoiceStatus>))]
public enum InvoiceStatus
{
    /// <summary>A status this SDK version does not know (sent by a newer service).</summary>
    Unknown = 0,

    /// <summary>Waiting for payment.</summary>
    Pending,

    /// <summary>A payment is seen and waits for confirmations.</summary>
    Confirming,

    /// <summary>Confirmed total is below the amount; the invoice is still open.</summary>
    Underpaid,

    /// <summary>Paid in full (terminal).</summary>
    Paid,

    /// <summary>Confirmed total exceeds the amount (terminal); the merchant decides what to do.</summary>
    Overpaid,

    /// <summary>The deadline passed; a late payment is still possible.</summary>
    Expired,

    /// <summary>Paid in full after the deadline (terminal); the merchant decides whether to honour it.</summary>
    PaidLate,

    /// <summary>Cancelled by the merchant (terminal).</summary>
    Cancelled,
}

/// <summary>Helpers for <see cref="InvoiceStatus"/>.</summary>
public static class InvoiceStatusExtensions
{
    /// <summary>True for statuses that never change again: paid, overpaid, paid_late, cancelled.</summary>
    public static bool IsTerminal(this InvoiceStatus status) =>
        status is InvoiceStatus.Paid or InvoiceStatus.Overpaid or InvoiceStatus.PaidLate or InvoiceStatus.Cancelled;

    /// <summary>The wire name, e.g. <c>paid_late</c>.</summary>
    public static string ToWireName(this InvoiceStatus status) => TolerantEnumConverter<InvoiceStatus>.NameOf(status);
}

/// <summary>State of an on-chain transfer to an invoice's deposit address.</summary>
[JsonConverter(typeof(TolerantEnumConverter<TransferState>))]
public enum TransferState
{
    /// <summary>Unknown to this SDK version.</summary>
    Unknown = 0,

    /// <summary>Seen on-chain, fewer than N confirmations.</summary>
    Seen,

    /// <summary>Final (N confirmations).</summary>
    Confirmed,

    /// <summary>Disappeared in a chain reorganization.</summary>
    Reverted,
}

/// <summary>Delivery state of a webhook event.</summary>
[JsonConverter(typeof(TolerantEnumConverter<EventDeliveryStatus>))]
public enum EventDeliveryStatus
{
    /// <summary>Unknown to this SDK version.</summary>
    Unknown = 0,

    /// <summary>Queued or waiting for a retry.</summary>
    Pending,

    /// <summary>Acknowledged with a 2xx by the merchant.</summary>
    Delivered,

    /// <summary>Retries exhausted; can be resent manually.</summary>
    Failed,
}

/// <summary>Why a transfer matched no invoice.</summary>
[JsonConverter(typeof(TolerantEnumConverter<UnmatchedReason>))]
public enum UnmatchedReason
{
    /// <summary>Unknown to this SDK version.</summary>
    Unknown = 0,

    /// <summary>The receiving address belongs to no invoice.</summary>
    UnknownAddress,

    /// <summary>The address belongs to an invoice that was already closed.</summary>
    InvoiceClosed,
}
