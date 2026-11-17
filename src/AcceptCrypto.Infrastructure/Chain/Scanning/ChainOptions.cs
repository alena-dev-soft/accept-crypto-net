namespace AcceptCrypto.Infrastructure.Chain.Scanning;

/// <summary>
/// Chain and scanner configuration (section <c>Chain</c>). One chain and one token per deployment (MVP).
/// See docs/DECISIONS.md D14.
/// </summary>
public sealed class ChainOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Chain";

    /// <summary>
    /// Runs the scanner and the expiry worker as background services. Off by default so the service starts without
    /// a chain (e.g. the Preview); the compose stack turns it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>JSON-RPC endpoint. May contain a provider API key: keep it in secrets, it is never logged (NFR-5).</summary>
    public Uri? RpcUrl { get; set; }

    /// <summary>EIP-155 chain ID; checked against <c>eth_chainId</c> before scanning. Also stamped on stored transfers.</summary>
    public long ChainId { get; set; } = 31337;

    /// <summary>ERC-20 token contract (6-decimal USDC or the local mock).</summary>
    public string? TokenAddress { get; set; }

    /// <summary>
    /// N: a transfer is final when <c>head - block + 1 &gt;= N</c> (FR-7, D4). Must exceed any reorg depth you expect:
    /// a confirmed transfer is never reverted.
    /// </summary>
    public int Confirmations { get; set; } = 2;

    /// <summary>Maximum <c>eth_getLogs</c> block range per request (RPC provider limits, FR-9).</summary>
    public int MaxBlockRange { get; set; } = 500;

    /// <summary>Maximum deposit addresses OR-ed into one <c>eth_getLogs</c> topic filter; more addresses = more requests.</summary>
    public int MaxAddressesPerQuery { get; set; } = 100;

    /// <summary>
    /// First block to scan when there is no checkpoint yet. Null = the chain head at the first start
    /// (invoices cannot predate the service). Use the token deployment block to scan history.
    /// </summary>
    public long? StartBlock { get; set; }

    /// <summary>How many blocks the checkpoint is rolled back when a reorg is detected (re-scanning is idempotent).</summary>
    public int ReorgRollbackDepth { get; set; } = 32;

    /// <summary>Delay between scan passes when healthy.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>First retry delay after a failed pass (RPC or database down); doubles per failure (NFR-4).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound of the retry delay.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout of one JSON-RPC request.</summary>
    public TimeSpan RpcTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Expiry worker interval.</summary>
    public TimeSpan ExpiryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Invoices expired per expiry batch.</summary>
    public int ExpiryBatchSize { get; set; } = 100;

    /// <summary>The scanner health check reports Degraded when no pass succeeded for this long.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);

    internal bool IsValid(out string error)
    {
        error = "";
        if (RpcUrl is null || !RpcUrl.IsAbsoluteUri)
        {
            error = "Chain:RpcUrl must be an absolute URL.";
        }
        else if (!IsAddress(TokenAddress))
        {
            error = "Chain:TokenAddress must be a 0x-prefixed 20-byte address.";
        }
        else if (ChainId <= 0 || Confirmations < 1 || MaxBlockRange < 1 || MaxAddressesPerQuery < 1 || ReorgRollbackDepth < 0 || ExpiryBatchSize < 1)
        {
            error = "Chain: ChainId, Confirmations, MaxBlockRange, MaxAddressesPerQuery and ExpiryBatchSize must be positive; ReorgRollbackDepth non-negative.";
        }
        else if (StartBlock < 0)
        {
            error = "Chain:StartBlock must be non-negative.";
        }
        else if (PollInterval <= TimeSpan.Zero || RetryBaseDelay <= TimeSpan.Zero || RetryMaxDelay < RetryBaseDelay
                 || RpcTimeout <= TimeSpan.Zero || ExpiryInterval <= TimeSpan.Zero)
        {
            error = "Chain: intervals and timeouts must be positive and RetryMaxDelay >= RetryBaseDelay.";
        }

        return error.Length == 0;
    }

    private static bool IsAddress(string? value)
    {
        try
        {
            Domain.Chain.EvmHex.NormalizeAddress(value ?? "", nameof(TokenAddress));
            return true;
        }
        catch (Domain.DomainException)
        {
            return false;
        }
    }
}
