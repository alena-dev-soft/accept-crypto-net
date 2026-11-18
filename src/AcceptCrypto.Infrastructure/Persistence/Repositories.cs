using AcceptCrypto.Domain.Chain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>Unmatched payments (FR-11a, S7).</summary>
public interface IUnmatchedPaymentRepository
{
    /// <summary>Records a transfer as unmatched; idempotent on (chain_id, tx_hash, log_index). Returns false if it was already recorded.</summary>
    Task<bool> AddAsync(ChainTransfer transfer, UnmatchedReason reason, Guid? invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Lists unmatched payments, newest first; <paramref name="beforeId"/> is the cursor (last ID of the previous page).</summary>
    Task<IReadOnlyList<UnmatchedPayment>> ListAsync(long? beforeId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Scanner checkpoint (FR-9).</summary>
public interface IScannerCheckpointRepository
{
    /// <summary>The checkpoint of the configured chain, or null before the first scan.</summary>
    Task<ScannerCheckpoint?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Upserts the checkpoint of the configured chain (may move backwards after a reorg).</summary>
    Task SaveAsync(long lastScannedBlock, string? lastScannedBlockHash, CancellationToken cancellationToken = default);
}

/// <summary>Webhook outbox access for the dispatcher and the API (FR-14, FR-15, FR-19).</summary>
public interface IOutboxRepository
{
    /// <summary>Pending events due at <paramref name="now"/>, in causal (insertion) order.</summary>
    Task<IReadOnlyList<OutboxEvent>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>All events of an invoice with their delivery attempts, in order (FR-19).</summary>
    Task<IReadOnlyList<OutboxEvent>> ListByInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads an event (with its delivery attempts), applies <paramref name="change"/> and saves it, guarded by the row version (xmin).
    /// Returns null if the event does not exist; throws <see cref="DbUpdateConcurrencyException"/> if it changed meanwhile.
    /// </summary>
    Task<OutboxEvent?> UpdateAsync(Guid eventId, Action<OutboxEvent> change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="limit"/> due pending events for delivery, in causal order, safe under parallel
    /// dispatchers: <c>FOR UPDATE SKIP LOCKED</c> picks rows nobody else is claiming, and the claim moves their
    /// <c>next_attempt_at</c> to <paramref name="leaseUntil"/>, so no other dispatcher takes them while the HTTP
    /// request runs. If the claimer dies, the lease runs out and the event is picked up again (at-least-once).
    /// </summary>
    Task<IReadOnlyList<OutboxEvent>> ClaimDueAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int limit, CancellationToken cancellationToken = default);

    /// <summary>Queue statistics for metrics (NFR-7).</summary>
    Task<OutboxQueueStats> GetQueueStatsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>Webhook queue statistics.</summary>
/// <param name="Pending">Events waiting for delivery (due or scheduled for a retry).</param>
/// <param name="Due">Pending events whose next attempt is due now.</param>
/// <param name="Failed">Events that exhausted their retries (FR-14).</param>
/// <param name="OldestPendingCreatedAt">Creation time of the oldest pending event, if any.</param>
public sealed record OutboxQueueStats(long Pending, long Due, long Failed, DateTimeOffset? OldestPendingCreatedAt);

/// <summary>EF Core implementation of <see cref="IUnmatchedPaymentRepository"/>.</summary>
public sealed class UnmatchedPaymentRepository(
    IDbContextFactory<AcceptCryptoDbContext> contextFactory,
    IOptions<PersistenceOptions> options,
    TimeProvider clock) : IUnmatchedPaymentRepository
{
    /// <inheritdoc />
    public async Task<bool> AddAsync(ChainTransfer transfer, UnmatchedReason reason, Guid? invoiceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var p = new UnmatchedPayment(options.Value.ChainId, transfer, reason, invoiceId, clock.GetUtcNow());
        var rows = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO unmatched_payments
                (chain_id, tx_hash, log_index, block_number, block_hash, block_timestamp,
                 from_address, to_address, amount, reason, invoice_id, recorded_at)
            VALUES ({p.ChainId}, {p.TxHash}, {p.LogIndex}, {p.BlockNumber}, {p.BlockHash}, {p.BlockTimestamp.ToUniversalTime()},
                    {p.FromAddress}, {p.ToAddress}, {p.Amount.BaseUnits}, {p.Reason.ToString()}, {p.InvoiceId}, {p.RecordedAt.ToUniversalTime()})
            ON CONFLICT (chain_id, tx_hash, log_index) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnmatchedPayment>> ListAsync(long? beforeId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var q = db.UnmatchedPayments.AsNoTracking().Where(u => u.ChainId == options.Value.ChainId);
        if (beforeId is { } cursor)
        {
            q = q.Where(u => u.Id < cursor);
        }

        return await q.OrderByDescending(u => u.Id).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IScannerCheckpointRepository"/>.</summary>
public sealed class ScannerCheckpointRepository(
    IDbContextFactory<AcceptCryptoDbContext> contextFactory,
    IOptions<PersistenceOptions> options,
    TimeProvider clock) : IScannerCheckpointRepository
{
    /// <inheritdoc />
    public async Task<ScannerCheckpoint?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ScannerCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(c => c.ChainId == options.Value.ChainId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(long lastScannedBlock, string? lastScannedBlockHash, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastScannedBlock);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO scanner_checkpoints (chain_id, last_scanned_block, last_scanned_block_hash, updated_at)
            VALUES ({options.Value.ChainId}, {lastScannedBlock}, {lastScannedBlockHash}, {now})
            ON CONFLICT (chain_id) DO UPDATE
            SET last_scanned_block = EXCLUDED.last_scanned_block,
                last_scanned_block_hash = EXCLUDED.last_scanned_block_hash,
                updated_at = EXCLUDED.updated_at
            """,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IOutboxRepository"/>.</summary>
public sealed class OutboxRepository(IDbContextFactory<AcceptCryptoDbContext> contextFactory) : IOutboxRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxEvent>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.OutboxEvents.AsNoTracking()
            .Where(e => e.Status == OutboxEventStatus.Pending && e.NextAttemptAt <= now)
            .OrderBy(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxEvent>> ListByInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.OutboxEvents.AsNoTracking()
            .Include(e => e.DeliveryAttempts.OrderBy(a => a.Id))
            .Where(e => e.InvoiceId == invoiceId)
            .OrderBy(e => e.Id)
            .AsSplitQuery()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OutboxEvent?> UpdateAsync(Guid eventId, Action<OutboxEvent> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var outboxEvent = await db.OutboxEvents.Include(e => e.DeliveryAttempts.OrderBy(a => a.Id)).SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken).ConfigureAwait(false);
        if (outboxEvent is null)
        {
            return null;
        }

        change(outboxEvent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return outboxEvent;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxEvent>> ClaimDueAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = nameof(OutboxEventStatus.Pending);

        // One statement = one transaction: lock due rows (skipping rows another dispatcher holds), push them behind
        // the lease and return them. The CTE keeps the causal order for the caller.
        var claimed = await db.OutboxEvents.FromSql($"""
            WITH due AS (
                SELECT id FROM outbox_events
                WHERE status = {pending} AND next_attempt_at <= {now}
                ORDER BY id
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED)
            UPDATE outbox_events o SET next_attempt_at = {leaseUntil}
            FROM due WHERE o.id = due.id
            RETURNING o.*, o.xmin
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. claimed.OrderBy(e => e.Id)];
    }

    /// <inheritdoc />
    public async Task<OutboxQueueStats> GetQueueStatsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stats = await db.OutboxEvents.AsNoTracking()
            .Where(e => e.Status != OutboxEventStatus.Delivered)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Pending = g.LongCount(e => e.Status == OutboxEventStatus.Pending),
                Due = g.LongCount(e => e.Status == OutboxEventStatus.Pending && e.NextAttemptAt <= now),
                Failed = g.LongCount(e => e.Status == OutboxEventStatus.Failed),
                Oldest = g.Where(e => e.Status == OutboxEventStatus.Pending).Min(e => (DateTimeOffset?)e.CreatedAt),
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return stats is null
            ? new OutboxQueueStats(0, 0, 0, null)
            : new OutboxQueueStats(stats.Pending, stats.Due, stats.Failed, stats.Oldest);
    }
}
