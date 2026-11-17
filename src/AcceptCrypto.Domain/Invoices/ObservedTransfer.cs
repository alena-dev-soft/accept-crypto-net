using System.Diagnostics.CodeAnalysis;
using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Invoices;

/// <summary>State of a transfer recorded on an invoice.</summary>
public enum TransferState
{
    /// <summary>Seen on-chain with fewer than N confirmations; does not count towards the paid total yet.</summary>
    Seen,

    /// <summary>Reached N confirmations; final, counts towards the paid total.</summary>
    Confirmed,

    /// <summary>Removed from the canonical chain by a reorg before it was confirmed. Kept for history (FR-19).</summary>
    Reverted,
}

/// <summary>A transfer to the invoice's deposit address, owned by the <see cref="Invoice"/> aggregate.</summary>
public sealed class ObservedTransfer
{
    internal ObservedTransfer(ChainTransfer transfer, bool isOnTime, DateTimeOffset now)
    {
        TxHash = transfer.Id.TxHash;
        LogIndex = transfer.Id.LogIndex;
        From = transfer.From;
        Amount = transfer.Amount;
        SeenAt = now;
        ApplyBlock(transfer, isOnTime);
    }

#pragma warning disable CS8618 // Persistence constructor: properties are set by the ORM.
    private ObservedTransfer()
#pragma warning restore CS8618
    {
    }

    /// <summary>Transaction hash + log index.</summary>
    public TransferId Id => new(TxHash, LogIndex);

    /// <summary>Transaction hash (lower case); with <see cref="LogIndex"/> it identifies the transfer.</summary>
    public string TxHash { get; private set; }

    /// <summary>Index of the <c>Transfer</c> log within the block.</summary>
    public int LogIndex { get; private set; }

    /// <summary>Sender address (lower case).</summary>
    public string From { get; private set; }

    /// <summary>Transferred amount.</summary>
    public UsdcAmount Amount { get; private set; }

    /// <summary>Block number of the (latest) inclusion.</summary>
    public long BlockNumber { get; private set; }

    /// <summary>Block hash of the (latest) inclusion.</summary>
    public string BlockHash { get; private set; }

    /// <summary>Block timestamp of the (latest) inclusion.</summary>
    public DateTimeOffset BlockTimestamp { get; private set; }

    /// <summary><c>true</c> if the block timestamp is before the invoice deadline (spec: status rules).</summary>
    public bool IsOnTime { get; private set; }

    /// <summary>Current state.</summary>
    public TransferState State { get; private set; }

    /// <summary>When the service first saw the transfer.</summary>
    public DateTimeOffset SeenAt { get; private set; }

    /// <summary>When the transfer reached N confirmations.</summary>
    public DateTimeOffset? ConfirmedAt { get; private set; }

    /// <summary>When the transfer was removed by a reorg (cleared if it is re-included).</summary>
    public DateTimeOffset? RevertedAt { get; private set; }

    internal void Confirm(DateTimeOffset now)
    {
        State = TransferState.Confirmed;
        ConfirmedAt = now;
    }

    internal void Revert(DateTimeOffset now)
    {
        State = TransferState.Reverted;
        RevertedAt = now;
    }

    /// <summary>The same log was re-included in a new canonical block after a reorg.</summary>
    internal void Reinstate(ChainTransfer transfer, bool isOnTime)
    {
        ApplyBlock(transfer, isOnTime);
        RevertedAt = null;
    }

    [MemberNotNull(nameof(BlockHash))]
    private void ApplyBlock(ChainTransfer transfer, bool isOnTime)
    {
        BlockNumber = transfer.BlockNumber;
        BlockHash = transfer.BlockHash;
        BlockTimestamp = transfer.BlockTimestamp;
        IsOnTime = isOnTime;
        State = TransferState.Seen;
    }
}
