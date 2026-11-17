using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>Why a transfer could not be matched to an open invoice (FR-11a).</summary>
public enum UnmatchedReason
{
    /// <summary>The recipient is not a known deposit address.</summary>
    UnknownAddress,

    /// <summary>The recipient belongs to an invoice in a terminal status (D5).</summary>
    InvoiceClosed,
}

/// <summary>A transfer that matches no open invoice, kept for manual reconciliation (FR-11a, S7).</summary>
public sealed class UnmatchedPayment
{
    /// <summary>Creates an unmatched payment record.</summary>
    public UnmatchedPayment(long chainId, ChainTransfer transfer, UnmatchedReason reason, Guid? invoiceId, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ChainId = chainId;
        TxHash = transfer.Id.TxHash;
        LogIndex = transfer.Id.LogIndex;
        BlockNumber = transfer.BlockNumber;
        BlockHash = transfer.BlockHash;
        BlockTimestamp = transfer.BlockTimestamp;
        FromAddress = transfer.From;
        ToAddress = transfer.To;
        Amount = transfer.Amount;
        Reason = reason;
        InvoiceId = invoiceId;
        RecordedAt = recordedAt;
    }

#pragma warning disable CS8618 // Persistence constructor.
    private UnmatchedPayment()
#pragma warning restore CS8618
    {
    }

    /// <summary>Surrogate key, increasing in insertion order (used as the list cursor).</summary>
    public long Id { get; private set; }

    /// <summary>EVM chain ID.</summary>
    public long ChainId { get; private set; }

    /// <summary>Transaction hash (lower case).</summary>
    public string TxHash { get; private set; }

    /// <summary>Log index within the block.</summary>
    public int LogIndex { get; private set; }

    /// <summary>Block number.</summary>
    public long BlockNumber { get; private set; }

    /// <summary>Block hash (lower case).</summary>
    public string BlockHash { get; private set; }

    /// <summary>Block timestamp.</summary>
    public DateTimeOffset BlockTimestamp { get; private set; }

    /// <summary>Sender.</summary>
    public string FromAddress { get; private set; }

    /// <summary>Recipient.</summary>
    public string ToAddress { get; private set; }

    /// <summary>Amount.</summary>
    public UsdcAmount Amount { get; private set; }

    /// <summary>Why it is unmatched.</summary>
    public UnmatchedReason Reason { get; private set; }

    /// <summary>The closed invoice the address belongs to, if any.</summary>
    public Guid? InvoiceId { get; private set; }

    /// <summary>When the service recorded it.</summary>
    public DateTimeOffset RecordedAt { get; private set; }
}

/// <summary>Where the chain scanner stopped (FR-9), one row per chain.</summary>
public sealed class ScannerCheckpoint
{
    /// <summary>Creates a checkpoint.</summary>
    public ScannerCheckpoint(long chainId, long lastScannedBlock, string? lastScannedBlockHash, DateTimeOffset updatedAt)
    {
        ChainId = chainId;
        LastScannedBlock = lastScannedBlock;
        LastScannedBlockHash = lastScannedBlockHash;
        UpdatedAt = updatedAt;
    }

    /// <summary>EVM chain ID (primary key).</summary>
    public long ChainId { get; private set; }

    /// <summary>Highest block whose logs are fully processed.</summary>
    public long LastScannedBlock { get; private set; }

    /// <summary>Hash of <see cref="LastScannedBlock"/>, to detect a reorg below the checkpoint.</summary>
    public string? LastScannedBlockHash { get; private set; }

    /// <summary>Last update time.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>An idempotency key of an invoice create request (FR-2).</summary>
public sealed class IdempotencyKeyRecord
{
#pragma warning disable CS8618 // Persistence constructor.
    private IdempotencyKeyRecord()
#pragma warning restore CS8618
    {
    }

    /// <summary>The key sent by the integrator (primary key).</summary>
    public string Key { get; private set; }

    /// <summary>Fingerprint of the request body; a replay with a different body is a conflict.</summary>
    public string RequestFingerprint { get; private set; }

    /// <summary>The invoice created by the first request.</summary>
    public Guid? InvoiceId { get; private set; }

    /// <summary>When the key was first used.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
