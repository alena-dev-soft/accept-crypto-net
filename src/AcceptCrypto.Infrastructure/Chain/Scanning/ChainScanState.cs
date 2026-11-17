using System.Diagnostics.Metrics;

namespace AcceptCrypto.Infrastructure.Chain.Scanning;

/// <summary>
/// In-process view of the scanner's progress, shared with the expiry worker and the health check, and exported as
/// metrics (meter <see cref="MeterName"/>, NFR-7): lag behind the chain head, head/scanned block, transfers,
/// reorgs, failed passes.
/// </summary>
public sealed class ChainScanState
{
    /// <summary>Name of the <see cref="Meter"/>.</summary>
    public const string MeterName = "AcceptCrypto.Chain";

    private readonly Lock _lock = new();
    private readonly Counter<long> _transfers;
    private readonly Counter<long> _reorgs;
    private readonly Counter<long> _failures;
    private long? _head;
    private long? _lastScannedBlock;
    private DateTimeOffset? _lastScannedBlockTimestamp;
    private DateTimeOffset? _caughtUpAt;
    private DateTimeOffset? _lastSuccessAt;
    private int _consecutiveFailures;

    /// <summary>Creates the state and its instruments.</summary>
    public ChainScanState(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("acceptcrypto.scanner.lag_blocks", () => LagBlocks ?? 0, "{block}", "Blocks between the chain head and the last scanned block.");
        meter.CreateObservableGauge("acceptcrypto.scanner.head_block", () => Head ?? 0, "{block}", "Latest chain head seen by the scanner.");
        meter.CreateObservableGauge("acceptcrypto.scanner.last_scanned_block", () => LastScannedBlock ?? 0, "{block}", "Last block scanned (checkpoint).");
        _transfers = meter.CreateCounter<long>("acceptcrypto.scanner.transfers", "{transfer}", "Token transfers to deposit addresses processed (new ones only).");
        _reorgs = meter.CreateCounter<long>("acceptcrypto.scanner.reorgs", "{reorg}", "Chain reorganisations detected.");
        _failures = meter.CreateCounter<long>("acceptcrypto.scanner.failed_passes", "{pass}", "Scan passes that failed (RPC or database errors).");
    }

    /// <summary>Latest head block number seen.</summary>
    public long? Head { get { lock (_lock) { return _head; } } }

    /// <summary>Last block scanned.</summary>
    public long? LastScannedBlock { get { lock (_lock) { return _lastScannedBlock; } } }

    /// <summary>Head minus last scanned block (0 when caught up).</summary>
    public long? LagBlocks
    {
        get
        {
            lock (_lock)
            {
                return _head is { } h && _lastScannedBlock is { } s ? Math.Max(0, h - s) : null;
            }
        }
    }

    /// <summary>When a pass last completed successfully (service clock).</summary>
    public DateTimeOffset? LastSuccessAt { get { lock (_lock) { return _lastSuccessAt; } } }

    /// <summary>Consecutive failed passes.</summary>
    public int ConsecutiveFailures { get { lock (_lock) { return _consecutiveFailures; } } }

    /// <summary>
    /// Every transfer included before this instant has been scanned: the later of the last scanned block's timestamp
    /// and the moment (service clock) the head was read in a pass that then caught up to it. Null until the first
    /// pass in this process. The expiry worker only expires invoices whose deadline is not after this (D11, D14).
    /// </summary>
    public DateTimeOffset? ScannedThrough
    {
        get
        {
            lock (_lock)
            {
                return (_lastScannedBlockTimestamp, _caughtUpAt) switch
                {
                    ({ } a, { } b) => a > b ? a : b,
                    ({ } a, null) => a,
                    (null, var b) => b,
                };
            }
        }
    }

    internal void ReportHead(long head)
    {
        lock (_lock)
        {
            _head = head;
        }
    }

    internal void ReportScanned(long block, DateTimeOffset blockTimestamp)
    {
        lock (_lock)
        {
            _lastScannedBlock = block;
            _lastScannedBlockTimestamp = blockTimestamp;
        }
    }

    internal void ReportPassCompleted(DateTimeOffset headObservedAt, bool caughtUp, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (caughtUp)
            {
                _caughtUpAt = headObservedAt;
            }

            _lastSuccessAt = now;
            _consecutiveFailures = 0;
        }
    }

    internal void ReportRollback(long lastScannedBlock)
    {
        lock (_lock)
        {
            _lastScannedBlock = lastScannedBlock;
        }

        _reorgs.Add(1);
    }

    internal void ReportTransfer() => _transfers.Add(1);

    internal int ReportFailure()
    {
        _failures.Add(1);
        lock (_lock)
        {
            return ++_consecutiveFailures;
        }
    }
}
