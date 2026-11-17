using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Chain;

/// <summary>Identity of an ERC-20 <c>Transfer</c> log: transaction hash + log index (FR-8).</summary>
/// <param name="TxHash">Lower-case <c>0x</c>-prefixed transaction hash.</param>
/// <param name="LogIndex">Index of the log within the block.</param>
public readonly record struct TransferId(string TxHash, int LogIndex)
{
    /// <inheritdoc />
    public override string ToString() => $"{TxHash}:{LogIndex}";
}

/// <summary>
/// An ERC-20 <c>Transfer</c> of the configured token as observed on-chain by the scanner.
/// Immutable; inputs are validated and hex values normalized to lower case.
/// </summary>
public sealed record ChainTransfer
{
    /// <summary>Creates a validated transfer observation.</summary>
    /// <exception cref="DomainException">Malformed hash/address, negative numbers or a zero amount.</exception>
    public ChainTransfer(
        string txHash,
        int logIndex,
        long blockNumber,
        string blockHash,
        DateTimeOffset blockTimestamp,
        string from,
        string to,
        UsdcAmount amount)
    {
        if (logIndex < 0 || blockNumber < 0)
        {
            throw new DomainException(DomainErrorCodes.InvalidChainData, "Log index and block number must be non-negative.");
        }

        // Zero-value transfers are address-poisoning spam; the scanner drops them (docs/DECISIONS.md D11).
        if (amount.IsZero)
        {
            throw new DomainException(DomainErrorCodes.InvalidChainData, "A transfer must have a positive amount.");
        }

        Id = new TransferId(EvmHex.NormalizeHash(txHash, nameof(txHash)), logIndex);
        BlockNumber = blockNumber;
        BlockHash = EvmHex.NormalizeHash(blockHash, nameof(blockHash));
        BlockTimestamp = blockTimestamp;
        From = EvmHex.NormalizeAddress(from, nameof(from));
        To = EvmHex.NormalizeAddress(to, nameof(to));
        Amount = amount;
    }

    /// <summary>Transaction hash + log index.</summary>
    public TransferId Id { get; }

    /// <summary>Block number the log was included in.</summary>
    public long BlockNumber { get; }

    /// <summary>Hash of that block (used to detect reorgs).</summary>
    public string BlockHash { get; }

    /// <summary>Block timestamp; decides whether the payment is on time.</summary>
    public DateTimeOffset BlockTimestamp { get; }

    /// <summary>Sender address (lower case).</summary>
    public string From { get; }

    /// <summary>Recipient address (lower case).</summary>
    public string To { get; }

    /// <summary>Transferred amount (positive).</summary>
    public UsdcAmount Amount { get; }
}
