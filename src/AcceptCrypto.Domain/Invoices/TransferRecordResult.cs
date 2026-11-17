namespace AcceptCrypto.Domain.Invoices;

/// <summary>What happened to a transfer offered to <see cref="Invoice.RecordTransfer"/>.</summary>
public enum TransferRecordOutcome
{
    /// <summary>Recorded (or re-included after a reorg); the invoice may have changed status.</summary>
    Accepted,

    /// <summary>Already recorded and active — nothing changed (FR-8: never counted twice).</summary>
    Duplicate,

    /// <summary>The invoice is terminal; the caller records the transfer as an unmatched payment (FR-11a).</summary>
    InvoiceClosed,
}

/// <summary>Result of <see cref="Invoice.RecordTransfer"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Events">Events produced (empty unless <see cref="TransferRecordOutcome.Accepted"/>).</param>
public sealed record TransferRecordResult(TransferRecordOutcome Outcome, IReadOnlyList<InvoiceDomainEvent> Events);
