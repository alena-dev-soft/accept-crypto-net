using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>Outcome of an idempotent invoice creation (FR-2).</summary>
public enum InvoiceCreateOutcome
{
    /// <summary>A new invoice was created.</summary>
    Created,

    /// <summary>The idempotency key was used before with the same request: the original invoice is returned.</summary>
    Replayed,

    /// <summary>The idempotency key was used before with a different request: the original invoice is returned, nothing is created.</summary>
    KeyReusedWithDifferentRequest,
}

/// <summary>Result of <see cref="IInvoiceRepository.CreateAsync"/>.</summary>
public sealed record InvoiceCreateResult(InvoiceCreateOutcome Outcome, Invoice Invoice);

/// <summary>What an invoice mutation returns: a result for the caller and the domain events to put in the outbox.</summary>
public sealed record InvoiceMutation<T>(T Result, IReadOnlyList<InvoiceDomainEvent> Events);

/// <summary>Result of a committed invoice update.</summary>
public sealed record InvoiceUpdate<T>(Invoice Invoice, T Result, IReadOnlyList<InvoiceDomainEvent> Events);

/// <summary>Keyset cursor of the invoice list: the last row of the previous page.</summary>
public sealed record InvoiceCursor(DateTimeOffset CreatedAt, Guid Id);

/// <summary>Invoice list query (FR-18). Newest first.</summary>
public sealed record InvoiceListQuery(
    InvoiceStatus? Status = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    InvoiceCursor? After = null,
    int Limit = 50);

/// <summary>A page of invoices; <see cref="Next"/> is null on the last page.</summary>
public sealed record InvoicePage(IReadOnlyList<Invoice> Items, InvoiceCursor? Next);

/// <summary>Invoice storage and the unit of work for invoice changes.</summary>
public interface IInvoiceRepository
{
    /// <summary>
    /// Creates an invoice, idempotently when <paramref name="idempotencyKey"/> is given (FR-2). The factory gets the
    /// next derivation index (BIP32 child index / invoice number) and builds the invoice; it runs inside the
    /// transaction, so a validation error leaves no key behind. Parallel requests with the same key create one invoice.
    /// </summary>
    Task<InvoiceCreateResult> CreateAsync(
        string? idempotencyKey,
        string requestFingerprint,
        Func<long, Invoice> factory,
        CancellationToken cancellationToken = default);

    /// <summary>Loads an invoice with its transfers (read-only snapshot).</summary>
    Task<Invoice?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Loads an invoice by its payment page token (FR-29).</summary>
    Task<Invoice?> GetByPublicTokenAsync(string publicToken, CancellationToken cancellationToken = default);

    /// <summary>Finds the invoice that owns a deposit address (lower case), for matching transfers (FR-5).</summary>
    Task<Invoice?> FindByDepositAddressAsync(string depositAddress, CancellationToken cancellationToken = default);

    /// <summary>IDs of open invoices (pending/underpaid) whose deadline has passed, oldest first.</summary>
    Task<IReadOnlyList<Guid>> ListDueForExpiryAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every deposit address ever issued (lower case). The chain scanner watches all of them: open invoices for
    /// payments, closed ones to record late money as unmatched (FR-11a).
    /// </summary>
    Task<IReadOnlyList<string>> ListDepositAddressesAsync(CancellationToken cancellationToken = default);

    /// <summary>Transfers seen on-chain but not yet confirmed (state Seen), oldest block first, for reorg checks and confirmation.</summary>
    Task<IReadOnlyList<UnconfirmedTransfer>> ListUnconfirmedTransfersAsync(CancellationToken cancellationToken = default);

    /// <summary>Cursor-paginated invoice list, newest first (FR-18).</summary>
    Task<InvoicePage> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unit of work for one invoice: loads it, applies <paramref name="mutate"/>, and commits the invoice change and
    /// the outbox rows for the returned events in <b>one transaction</b> (NFR-2). On an optimistic concurrency
    /// conflict (another writer committed first) the invoice is reloaded and <paramref name="mutate"/> runs again,
    /// up to <see cref="PersistenceOptions.MaxConcurrencyRetries"/> times (NFR-3). Returns null if the invoice does not exist.
    /// </summary>
    /// <exception cref="Domain.DomainException">Thrown by the mutation; nothing is saved.</exception>
    /// <exception cref="DbUpdateConcurrencyException">Still conflicting after all retries.</exception>
    Task<InvoiceUpdate<T>?> UpdateAsync<T>(Guid id, Func<Invoice, InvoiceMutation<T>> mutate, CancellationToken cancellationToken = default);
}

/// <summary>A not yet confirmed transfer of an invoice, as stored (block hash at the time it was seen).</summary>
public sealed record UnconfirmedTransfer(Guid InvoiceId, TransferId TransferId, long BlockNumber, string BlockHash);

/// <summary>EF Core / PostgreSQL implementation. Each call uses its own short-lived context, so it is thread-safe.</summary>
public sealed class InvoiceRepository(IDbContextFactory<AcceptCryptoDbContext> contextFactory, IOptions<PersistenceOptions> options, TimeProvider clock)
    : IInvoiceRepository
{
    private readonly int _maxRetries = options?.Value.MaxConcurrencyRetries ?? throw new ArgumentNullException(nameof(options));
    private readonly long _chainId = options.Value.ChainId;

    /// <inheritdoc />
    public async Task<InvoiceCreateResult> CreateAsync(
        string? idempotencyKey,
        string requestFingerprint,
        Func<long, Invoice> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestFingerprint);
        ArgumentNullException.ThrowIfNull(factory);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (idempotencyKey is not null)
        {
            var now = clock.GetUtcNow();

            // If another transaction holds the same key uncommitted, this INSERT waits for it; after its commit
            // the conflict is reported (0 rows) and we return its invoice; after its rollback we insert.
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO idempotency_keys (key, request_fingerprint, created_at)
                VALUES ({idempotencyKey}, {requestFingerprint}, {now})
                ON CONFLICT (key) DO NOTHING
                """,
                cancellationToken).ConfigureAwait(false);

            if (inserted == 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var record = await db.IdempotencyKeys.AsNoTracking()
                    .SingleAsync(k => k.Key == idempotencyKey, cancellationToken).ConfigureAwait(false);
                var original = await LoadAsync(db, record.InvoiceId!.Value, cancellationToken).ConfigureAwait(false);
                var outcome = string.Equals(record.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
                    ? InvoiceCreateOutcome.Replayed
                    : InvoiceCreateOutcome.KeyReusedWithDifferentRequest;
                return new InvoiceCreateResult(outcome, original!);
            }
        }

        // nextval is not transactional: a rolled-back create leaves a gap, i.e. an unused deposit address. Harmless.
        var index = await db.Database
            .SqlQuery<long>($"SELECT nextval({AcceptCryptoDbContext.DerivationIndexSequence}::regclass) AS \"Value\"")
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var invoice = factory(index);
        if (!string.Equals(invoice.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) || invoice.DerivationIndex != index)
        {
            throw new InvalidOperationException("The factory must use the given derivation index and idempotency key.");
        }

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (idempotencyKey is not null)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE idempotency_keys SET invoice_id = {invoice.Id} WHERE key = {idempotencyKey}",
                cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new InvoiceCreateResult(InvoiceCreateOutcome.Created, invoice);
    }

    /// <inheritdoc />
    public async Task<Invoice?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(db, id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Invoice?> GetByPublicTokenAsync(string publicToken, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.PublicToken == publicToken, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Invoice?> FindByDepositAddressAsync(string depositAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(depositAddress);
        var address = depositAddress.ToLowerInvariant();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.DepositAddress == address, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListDueForExpiryAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Invoices.AsNoTracking()
            .Where(i => (i.Status == InvoiceStatus.Pending || i.Status == InvoiceStatus.Underpaid) && i.ExpiresAt <= now)
            .OrderBy(i => i.ExpiresAt)
            .Select(i => i.Id)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListDepositAddressesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Invoices.AsNoTracking().Select(i => i.DepositAddress).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnconfirmedTransfer>> ListUnconfirmedTransfersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Invoices.AsNoTracking()
            .SelectMany(i => i.Transfers
                .Where(t => t.State == TransferState.Seen && EF.Property<long>(t, AcceptCryptoDbContext.ChainIdProperty) == _chainId)
                .Select(t => new { InvoiceId = i.Id, t.TxHash, t.LogIndex, t.BlockNumber, t.BlockHash }))
            .OrderBy(t => t.BlockNumber).ThenBy(t => t.TxHash).ThenBy(t => t.LogIndex)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ConvertAll(r => new UnconfirmedTransfer(r.InvoiceId, new TransferId(r.TxHash, r.LogIndex), r.BlockNumber, r.BlockHash));
    }

    /// <inheritdoc />
    public async Task<InvoicePage> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var q = db.Invoices.AsNoTracking();
        if (query.Status is { } status)
        {
            q = q.Where(i => i.Status == status);
        }

        if (query.CreatedFrom is { } from)
        {
            q = q.Where(i => i.CreatedAt >= from);
        }

        if (query.CreatedTo is { } to)
        {
            q = q.Where(i => i.CreatedAt < to);
        }

        if (query.After is { } after)
        {
            // Row-value comparison (created_at, id) < (@c, @i): stable keyset pagination.
            q = q.Where(i => EF.Functions.LessThan(ValueTuple.Create(i.CreatedAt, i.Id), ValueTuple.Create(after.CreatedAt, after.Id)));
        }

        var rows = await q
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        InvoiceCursor? next = null;
        if (rows.Count > query.Limit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = new InvoiceCursor(rows[^1].CreatedAt, rows[^1].Id);
        }

        return new InvoicePage(rows, next);
    }

    /// <inheritdoc />
    public async Task<InvoiceUpdate<T>?> UpdateAsync<T>(Guid id, Func<Invoice, InvoiceMutation<T>> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
            if (invoice is null)
            {
                return null;
            }

            var mutation = mutate(invoice);
            foreach (var domainEvent in mutation.Events)
            {
                // Added in causal order; the identity column of outbox_events preserves it.
                db.OutboxEvents.Add(OutboxEventFactory.Create(invoice, domainEvent));
            }

            try
            {
                // One SaveChanges = one transaction: invoice row, transfers and outbox rows commit together.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new InvoiceUpdate<T>(invoice, mutation.Result, mutation.Events);
            }
            catch (DbUpdateConcurrencyException) when (attempt < _maxRetries)
            {
                // Another writer changed the invoice since we loaded it: reload and re-apply.
            }
            catch (DbUpdateException ex) when (attempt < _maxRetries && PostgresErrors.IsUniqueViolation(ex))
            {
                // A parallel writer inserted the same transfer (chain_id, tx_hash, log_index) first (FR-8):
                // reload; the domain then reports it as a duplicate.
            }
        }
    }

    private static Task<Invoice?> LoadAsync(AcceptCryptoDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
}
