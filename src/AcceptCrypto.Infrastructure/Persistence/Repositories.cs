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
    /// Loads an event, applies <paramref name="change"/> and saves it, guarded by the row version (xmin).
    /// Returns null if the event does not exist; throws <see cref="DbUpdateConcurrencyException"/> if it changed meanwhile.
    /// </summary>
    Task<OutboxEvent?> UpdateAsync(Guid eventId, Action<OutboxEvent> change, CancellationToken cancellationToken = default);
}

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
        var outboxEvent = await db.OutboxEvents.SingleOrDefaultAsync(e => e.EventId == eventId, cancellationToken).ConfigureAwait(false);
        if (outboxEvent is null)
        {
            return null;
        }

        change(outboxEvent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return outboxEvent;
    }
}
