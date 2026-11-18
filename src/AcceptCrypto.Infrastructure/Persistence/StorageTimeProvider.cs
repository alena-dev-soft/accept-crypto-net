namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>
/// The system clock truncated to whole microseconds — the precision of PostgreSQL <c>timestamptz</c>.
/// Without it, a value taken from the clock (7 fractional digits) and the same value read back from the database
/// (6 digits) differ: e.g. the <c>201</c> response of a create and its idempotent replay (FR-2) would show
/// <c>…:00.9175839</c> vs <c>…:00.917583</c>. With it, an object returned from memory equals the stored one.
/// </summary>
public sealed class StorageTimeProvider : TimeProvider
{
    /// <summary>Shared instance.</summary>
    public static StorageTimeProvider Instance { get; } = new();

    private StorageTimeProvider()
    {
    }

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;

    /// <inheritdoc />
    public override long TimestampFrequency => System.TimestampFrequency;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Truncate(System.GetUtcNow());

    /// <inheritdoc />
    public override long GetTimestamp() => System.GetTimestamp();

    /// <summary>Drops the sub-microsecond part (100 ns ticks) of <paramref name="value"/>.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % (TimeSpan.TicksPerMillisecond / 1000)));
}
