using AcceptCrypto.Domain;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Chain.Scanning;

/// <summary>
/// Moves open invoices (pending/underpaid) past their deadline to <c>expired</c> (FR-3), each in one transaction
/// with its event. Only invoices whose deadline is not after <see cref="ChainScanState.ScannedThrough"/> are
/// expired: an on-time payment still in unscanned blocks would otherwise produce a spurious <c>expired</c> (D11).
/// The domain's <c>CheckDeadline</c> decides; it is a no-op for an invoice that is confirming meanwhile.
/// </summary>
public sealed partial class InvoiceExpiryProcessor(
    IInvoiceRepository invoices,
    ChainScanState state,
    IOptions<ChainOptions> options,
    TimeProvider clock,
    ILogger<InvoiceExpiryProcessor> logger)
{
    /// <summary>Expires due invoices (repeats batches until none are left). Returns how many invoices changed status.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (state.ScannedThrough is not { } scannedThrough)
        {
            return 0; // The scanner has not completed a pass in this process yet.
        }

        var changed = 0;
        var seen = new HashSet<Guid>();
        while (true)
        {
            var now = clock.GetUtcNow();
            var cutoff = scannedThrough < now ? scannedThrough : now;
            var due = await invoices.ListDueForExpiryAsync(cutoff, options.Value.ExpiryBatchSize, cancellationToken).ConfigureAwait(false);
            var fresh = due.Where(seen.Add).ToList();
            if (fresh.Count == 0)
            {
                return changed;
            }

            foreach (var id in fresh)
            {
                try
                {
                    var update = await invoices.UpdateAsync(id, i => new InvoiceMutation<bool>(true, i.CheckDeadline(clock.GetUtcNow())), cancellationToken)
                        .ConfigureAwait(false);
                    if (update is { Events.Count: > 0 })
                    {
                        changed++;
                        LogExpired(logger, id, update.Invoice.Status);
                    }
                }
                catch (DomainException ex)
                {
                    LogSkipped(logger, id, ex.Message);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Invoice {InvoiceId} passed its deadline; now {Status}")]
    private static partial void LogExpired(ILogger logger, Guid invoiceId, Domain.Invoices.InvoiceStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invoice {InvoiceId} skipped by the expiry worker: {Reason}")]
    private static partial void LogSkipped(ILogger logger, Guid invoiceId, string reason);
}
