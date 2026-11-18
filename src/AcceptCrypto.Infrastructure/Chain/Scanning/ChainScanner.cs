using AcceptCrypto.Domain;
using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Chain.Scanning;

/// <summary>Outcome of one scan pass (for tests and logs).</summary>
/// <param name="Head">Chain head at the start of the pass.</param>
/// <param name="FromBlock">First block scanned in this pass (after any rollback); greater than <paramref name="ToBlock"/> if nothing was scanned.</param>
/// <param name="ToBlock">Last block scanned (the new checkpoint).</param>
/// <param name="NewTransfers">Transfers recorded for the first time on an invoice.</param>
/// <param name="Confirmed">Transfers confirmed.</param>
/// <param name="Reverted">Transfers reverted because their block left the canonical chain.</param>
/// <param name="Unmatched">Transfers recorded as unmatched payments.</param>
/// <param name="ReorgDetected">The checkpoint block or an unconfirmed transfer's block was replaced.</param>
public sealed record ScanPassResult(long Head, long FromBlock, long ToBlock, int NewTransfers, int Confirmed, int Reverted, int Unmatched, bool ReorgDetected);

/// <summary>
/// Detects incoming token transfers to deposit addresses (FR-6..FR-11a). One <see cref="RunPassAsync"/> call:
/// <list type="number">
/// <item>reads the head, then checks the checkpoint block hash; on a mismatch rolls the checkpoint back by
/// <see cref="ChainOptions.ReorgRollbackDepth"/> (FR-10);</item>
/// <item>re-checks the block hash of every unconfirmed transfer: replaced → reverted (<c>payment.reverted</c>,
/// invoice moves back) and the next scan starts below it; still canonical and N deep → confirmed (FR-7);</item>
/// <item>scans [checkpoint+1, head] with <c>eth_getLogs</c> in chunks of at most <see cref="ChainOptions.MaxBlockRange"/>
/// blocks, filtered on the recipient topic, records each transfer on its invoice (confirming it right away when it is
/// already N deep), and stores the checkpoint after each chunk (FR-9).</item>
/// </list>
/// Each invoice change commits together with its outbox events (<see cref="IInvoiceRepository.UpdateAsync{T}"/>, NFR-2);
/// re-processing a transfer is a no-op thanks to its (chain, tx hash, log index) identity (FR-8). Not thread-safe:
/// one pass at a time (the background service guarantees it). See docs/DECISIONS.md D14.
/// </summary>
public sealed partial class ChainScanner(
    IChainRpcClient rpc,
    IInvoiceRepository invoices,
    IUnmatchedPaymentRepository unmatched,
    IScannerCheckpointRepository checkpoints,
    IOptions<ChainOptions> options,
    ChainScanState state,
    TimeProvider clock,
    ILogger<ChainScanner> logger)
{
    private bool _chainIdVerified;

    /// <summary>Runs one pass. Throws on RPC/database errors; nothing is lost, the next pass resumes from the checkpoint.</summary>
    /// <exception cref="ChainRpcException">RPC failure, or the node is on a different chain than configured.</exception>
    public async Task<ScanPassResult> RunPassAsync(CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var token = o.TokenAddress ?? throw new InvalidOperationException("Chain:TokenAddress is not configured.");
        if (!_chainIdVerified)
        {
            var chainId = await rpc.GetChainIdAsync(cancellationToken).ConfigureAwait(false);
            if (chainId != o.ChainId)
            {
                throw new ChainRpcException($"The RPC node is on chain {chainId}, but Chain:ChainId is {o.ChainId}. Refusing to scan.");
            }

            _chainIdVerified = true;
        }

        // Head first, deposit addresses second: an invoice missing from the address list was created after the head
        // was read, so its payments can only be in later blocks (D14).
        var headObservedAt = clock.GetUtcNow();
        var head = await rpc.GetHeadBlockNumberAsync(cancellationToken).ConfigureAwait(false);
        state.ReportHead(head);

        var (next, reorg) = await ResolveNextBlockAsync(head, cancellationToken).ConfigureAwait(false);

        var verification = await VerifyUnconfirmedAsync(head, cancellationToken).ConfigureAwait(false);
        if (verification.LowestRevertedBlock is { } lowest)
        {
            reorg = true;
            var rescanFrom = Math.Max(MinScanBlock(o), lowest - o.ReorgRollbackDepth);
            if (rescanFrom < next)
            {
                next = await RollBackAsync(rescanFrom, cancellationToken).ConfigureAwait(false);
            }
        }

        var fromBlock = next;
        var addresses = next <= head
            ? await invoices.ListDepositAddressesAsync(cancellationToken).ConfigureAwait(false)
            : [];
        int recorded = 0, confirmed = verification.Confirmed, unmatchedCount = 0;
        while (next <= head)
        {
            var to = Math.Min(head, next + o.MaxBlockRange - 1);
            var chunk = await ScanChunkAsync(token, next, to, head, addresses, cancellationToken).ConfigureAwait(false);
            recorded += chunk.Recorded;
            confirmed += chunk.Confirmed;
            unmatchedCount += chunk.Unmatched;
            next = to + 1;
        }

        state.ReportPassCompleted(headObservedAt, caughtUp: next > head, clock.GetUtcNow());
        var result = new ScanPassResult(head, fromBlock, next - 1, recorded, confirmed, verification.Reverted, unmatchedCount, reorg);
        if (recorded + confirmed + verification.Reverted + unmatchedCount > 0)
        {
            LogPass(logger, result.FromBlock, result.ToBlock, head, recorded, confirmed, verification.Reverted, unmatchedCount);
        }

        return result;
    }

    private static long MinScanBlock(ChainOptions o) => o.StartBlock ?? 0;

    private async Task<(long Next, bool Reorg)> ResolveNextBlockAsync(long head, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var checkpoint = await checkpoints.GetAsync(cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return (o.StartBlock ?? head, false);
        }

        var last = checkpoint.LastScannedBlock;
        var block = last <= head ? await rpc.GetBlockAsync(last, cancellationToken).ConfigureAwait(false) : null;
        if (checkpoint.LastScannedBlockHash is null
            || (block is not null && string.Equals(block.Hash, checkpoint.LastScannedBlockHash, StringComparison.OrdinalIgnoreCase)))
        {
            if (block is not null)
            {
                state.ReportScanned(last, block.Timestamp);
            }

            return (last + 1, false);
        }

        // The checkpoint block is no longer canonical (or the chain is now shorter): every block from the fork point
        // up was replaced. Re-scan a safe window; recording is idempotent, vanished transfers are reverted by the
        // block-hash check of unconfirmed transfers.
        var rescanFrom = Math.Max(MinScanBlock(o), Math.Min(last, head) - o.ReorgRollbackDepth);
        LogReorg(logger, last, rescanFrom);
        return (await RollBackAsync(rescanFrom, cancellationToken).ConfigureAwait(false), true);
    }

    /// <summary>Persists the checkpoint just below <paramref name="rescanFrom"/> and returns the block to scan next.</summary>
    private async Task<long> RollBackAsync(long rescanFrom, CancellationToken cancellationToken)
    {
        var target = rescanFrom - 1;
        if (target >= 0)
        {
            var block = await rpc.GetBlockAsync(target, cancellationToken).ConfigureAwait(false)
                ?? throw new ChainRpcException($"Block {target} is not available.");
            await checkpoints.SaveAsync(target, block.Hash, cancellationToken).ConfigureAwait(false);
            state.ReportScanned(target, block.Timestamp);
        }

        state.ReportRollback(target);
        return rescanFrom;
    }

    private async Task<(int Confirmed, int Reverted, long? LowestRevertedBlock)> VerifyUnconfirmedAsync(long head, CancellationToken cancellationToken)
    {
        var confirmations = options.Value.Confirmations;
        var pending = await invoices.ListUnconfirmedTransfersAsync(cancellationToken).ConfigureAwait(false);
        int confirmed = 0, reverted = 0;
        long? lowestReverted = null;
        foreach (var t in pending)
        {
            if (t.BlockNumber > head)
            {
                continue; // A lagging node behind a load balancer; not evidence of a reorg.
            }

            // Fresh hash check right before deciding (FR-10): only positive evidence (a different block at that
            // height) reverts; an unknown block just waits. Nothing is confirmed without a matching hash.
            var block = await rpc.GetBlockAsync(t.BlockNumber, cancellationToken).ConfigureAwait(false);
            if (block is null)
            {
                continue;
            }

            try
            {
                if (!string.Equals(block.Hash, t.BlockHash, StringComparison.OrdinalIgnoreCase))
                {
                    await invoices.UpdateAsync(t.InvoiceId, i => new InvoiceMutation<bool>(true, i.RevertTransfer(t.TransferId, clock.GetUtcNow())), cancellationToken)
                        .ConfigureAwait(false);
                    LogReverted(logger, t.TransferId, t.InvoiceId, t.BlockNumber);
                    reverted++;
                    lowestReverted = Math.Min(lowestReverted ?? long.MaxValue, t.BlockNumber);
                }
                else if (head - t.BlockNumber + 1 >= confirmations)
                {
                    await invoices.UpdateAsync(t.InvoiceId, i => new InvoiceMutation<bool>(true, i.ConfirmTransfer(t.TransferId, clock.GetUtcNow())), cancellationToken)
                        .ConfigureAwait(false);
                    confirmed++;
                }
            }
            catch (DomainException ex)
            {
                // The transfer changed state since it was listed (e.g. another instance confirmed it). Skip it.
                LogTransferConflict(logger, t.TransferId, ex.Message);
            }
        }

        return (confirmed, reverted, lowestReverted);
    }

    private async Task<(int Recorded, int Confirmed, int Unmatched)> ScanChunkAsync(
        string token, long from, long to, long head, IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        // The chunk end's hash is read before the logs: if a reorg happens in between, the next pass sees a checkpoint
        // mismatch and re-scans.
        var end = await rpc.GetBlockAsync(to, cancellationToken).ConfigureAwait(false)
            ?? throw new ChainRpcException($"Block {to} is not available.");
        var blocks = new Dictionary<long, ChainBlock> { [to] = end };

        var logs = await GetLogsAsync(token, from, to, addresses, cancellationToken).ConfigureAwait(false);
        int recorded = 0, confirmed = 0, unmatchedCount = 0;
        foreach (var log in logs.OrderBy(l => l.BlockNumber).ThenBy(l => l.LogIndex))
        {
            if (log.Removed || log.Value.IsZero)
            {
                continue; // removed = reorged out; zero value = address-poisoning spam (D11).
            }

            if (!blocks.TryGetValue(log.BlockNumber, out var block))
            {
                block = await rpc.GetBlockAsync(log.BlockNumber, cancellationToken).ConfigureAwait(false)
                    ?? throw new ChainRpcException($"Block {log.BlockNumber} is not available.");
                blocks[log.BlockNumber] = block;
            }

            if (!string.Equals(block.Hash, log.BlockHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChainRpcException($"Block {log.BlockNumber} changed while scanning (reorg in progress); retrying.");
            }

            var transfer = new ChainTransfer(
                log.TxHash, log.LogIndex, log.BlockNumber, log.BlockHash, block.Timestamp, log.From, log.To, UsdcAmount.FromBaseUnits(log.Value));
            switch (await ApplyAsync(transfer, head, cancellationToken).ConfigureAwait(false))
            {
                case ApplyOutcome.Recorded:
                    recorded++;
                    break;
                case ApplyOutcome.RecordedAndConfirmed:
                    recorded++;
                    confirmed++;
                    break;
                case ApplyOutcome.Unmatched:
                    unmatchedCount++;
                    break;
            }
        }

        await checkpoints.SaveAsync(to, end.Hash, cancellationToken).ConfigureAwait(false);
        state.ReportScanned(to, end.Timestamp);
        return (recorded, confirmed, unmatchedCount);
    }

    private async Task<List<TransferLog>> GetLogsAsync(
        string token, long from, long to, IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        var result = new List<TransferLog>();
        foreach (var batch in addresses.Chunk(options.Value.MaxAddressesPerQuery))
        {
            await GetLogsSplittingAsync(token, from, to, batch, result, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Providers reject ranges that are too wide or return too many results with a JSON-RPC error; the range is then
    /// halved until it fits (down to one block).
    /// </summary>
    private async Task GetLogsSplittingAsync(
        string token, long from, long to, string[] batch, List<TransferLog> result, CancellationToken cancellationToken)
    {
        try
        {
            result.AddRange(await rpc.GetTransferLogsAsync(token, from, to, batch, cancellationToken).ConfigureAwait(false));
        }
        catch (ChainRpcException ex) when (ex.RpcErrorCode is not null && to > from)
        {
            var mid = from + ((to - from) / 2);
            LogSplit(logger, from, to, ex.Message);
            await GetLogsSplittingAsync(token, from, mid, batch, result, cancellationToken).ConfigureAwait(false);
            await GetLogsSplittingAsync(token, mid + 1, to, batch, result, cancellationToken).ConfigureAwait(false);
        }
    }

    private enum ApplyOutcome
    {
        Duplicate,
        Recorded,
        RecordedAndConfirmed,
        Unmatched,
    }

    private async Task<ApplyOutcome> ApplyAsync(ChainTransfer transfer, long head, CancellationToken cancellationToken)
    {
        var invoice = await invoices.FindByDepositAddressAsync(transfer.To, cancellationToken).ConfigureAwait(false);
        if (invoice is null)
        {
            // Not reachable with the recipient filter (every queried address belongs to an invoice); kept for safety.
            return await RecordUnmatchedAsync(transfer, UnmatchedReason.UnknownAddress, null, cancellationToken).ConfigureAwait(false);
        }

        var deep = head - transfer.BlockNumber + 1 >= options.Value.Confirmations;
        var update = await invoices.UpdateAsync(
            invoice.Id,
            i =>
            {
                var now = clock.GetUtcNow();
                var r = i.RecordTransfer(transfer, now);
                if (r.Outcome != TransferRecordOutcome.Accepted)
                {
                    return new InvoiceMutation<ApplyOutcome>(r.Outcome == TransferRecordOutcome.InvoiceClosed ? ApplyOutcome.Unmatched : ApplyOutcome.Duplicate, r.Events);
                }

                // Already N deep (catching up after downtime): confirm in the same transaction (one fewer event).
                return deep
                    ? new InvoiceMutation<ApplyOutcome>(ApplyOutcome.RecordedAndConfirmed, [.. r.Events, .. i.ConfirmTransfer(transfer.Id, now)])
                    : new InvoiceMutation<ApplyOutcome>(ApplyOutcome.Recorded, r.Events);
            },
            cancellationToken).ConfigureAwait(false);

        var outcome = update?.Result ?? ApplyOutcome.Duplicate;
        if (outcome == ApplyOutcome.Unmatched)
        {
            return await RecordUnmatchedAsync(transfer, UnmatchedReason.InvoiceClosed, invoice.Id, cancellationToken).ConfigureAwait(false);
        }

        if (outcome != ApplyOutcome.Duplicate)
        {
            state.ReportTransfer();
            LogRecorded(logger, transfer.Id, invoice.Id, transfer.Amount, transfer.BlockNumber, update!.Invoice.Status);
        }

        return outcome;
    }

    private async Task<ApplyOutcome> RecordUnmatchedAsync(ChainTransfer transfer, UnmatchedReason reason, Guid? invoiceId, CancellationToken cancellationToken)
    {
        if (!await unmatched.AddAsync(transfer, reason, invoiceId, cancellationToken).ConfigureAwait(false))
        {
            return ApplyOutcome.Duplicate;
        }

        state.ReportTransfer();
        LogUnmatched(logger, transfer.Id, transfer.To, transfer.Amount, reason);
        return ApplyOutcome.Unmatched;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanned blocks {From}..{To} (head {Head}): {Recorded} new, {Confirmed} confirmed, {Reverted} reverted, {Unmatched} unmatched")]
    private static partial void LogPass(ILogger logger, long from, long to, long head, int recorded, int confirmed, int reverted, int unmatched);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reorg detected: checkpoint block {Checkpoint} is no longer canonical; re-scanning from block {RescanFrom}")]
    private static partial void LogReorg(ILogger logger, long checkpoint, long rescanFrom);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transfer {TransferId} of invoice {InvoiceId} vanished from block {BlockNumber} (reorg); reverted")]
    private static partial void LogReverted(ILogger logger, TransferId transferId, Guid invoiceId, long blockNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transfer {TransferId} skipped: {Reason}")]
    private static partial void LogTransferConflict(ILogger logger, TransferId transferId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "eth_getLogs for blocks {From}..{To} rejected ({Reason}); splitting the range")]
    private static partial void LogSplit(ILogger logger, long from, long to, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transfer {TransferId} to invoice {InvoiceId}: {Amount} USDC in block {BlockNumber}; invoice is {Status}")]
    private static partial void LogRecorded(ILogger logger, TransferId transferId, Guid invoiceId, UsdcAmount amount, long blockNumber, InvoiceStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unmatched transfer {TransferId} to {Address}: {Amount} USDC ({Reason})")]
    private static partial void LogUnmatched(ILogger logger, TransferId transferId, string address, UsdcAmount amount, UnmatchedReason reason);
}
