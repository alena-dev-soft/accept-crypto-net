namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>Storage settings (configuration section <c>Persistence</c>).</summary>
public sealed class PersistenceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Persistence";

    /// <summary>EVM chain ID stamped on stored transfers (FR-8 key). Default: 31337 (Anvil).</summary>
    public long ChainId { get; set; } = 31337;

    /// <summary>How many times an invoice update is retried after an optimistic concurrency conflict (NFR-3).</summary>
    public int MaxConcurrencyRetries { get; set; } = 5;

    /// <summary>Apply EF Core migrations when the service starts (used by the one-command stack).</summary>
    public bool MigrateOnStartup { get; set; }
}
