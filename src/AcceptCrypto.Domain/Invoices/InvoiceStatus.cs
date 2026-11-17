namespace AcceptCrypto.Domain.Invoices;

/// <summary>
/// Invoice lifecycle status. See the status table in the spec and docs/DECISIONS.md (D5, D6).
/// </summary>
public enum InvoiceStatus
{
    /// <summary>Waiting for payment.</summary>
    Pending,

    /// <summary>A payment is seen but has fewer than N confirmations.</summary>
    Confirming,

    /// <summary>Confirmed total is below the amount; the invoice is still open.</summary>
    Underpaid,

    /// <summary>Confirmed total equals the amount.</summary>
    Paid,

    /// <summary>Confirmed total exceeds the amount; the merchant decides what to do.</summary>
    Overpaid,

    /// <summary>Deadline passed; may hold a partial confirmed amount. A late payment is still possible.</summary>
    Expired,

    /// <summary>Paid in full after the deadline; the merchant decides whether to honour it.</summary>
    PaidLate,

    /// <summary>Cancelled by the merchant while pending (decision D6).</summary>
    Cancelled,
}

/// <summary>Helpers for <see cref="InvoiceStatus"/>.</summary>
public static class InvoiceStatusExtensions
{
    /// <summary>Returns <c>true</c> when no further transition is possible.</summary>
    public static bool IsTerminal(this InvoiceStatus status) => status switch
    {
        InvoiceStatus.Paid or InvoiceStatus.Overpaid or InvoiceStatus.PaidLate or InvoiceStatus.Cancelled => true,
        _ => false,
    };
}
