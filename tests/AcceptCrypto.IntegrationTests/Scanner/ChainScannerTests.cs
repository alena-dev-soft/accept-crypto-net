using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.IntegrationTests.Chain;
using AcceptCrypto.IntegrationTests.Persistence;

namespace AcceptCrypto.IntegrationTests.Scanner;

/// <summary>
/// The chain scanner against a real Anvil chain and a real PostgreSQL database (S2, S3, S4, S7; FR-6..FR-11a).
/// N = 3 confirmations unless stated: a transfer in block b is final once head - b + 1 >= 3.
/// </summary>
[Collection(AnvilTestGroup.Name)]
public sealed class ChainScannerTests(AnvilFixture chain, PostgresFixture postgres)
{
    [Fact]
    public async Task Exact_payment_goes_confirming_then_paid_after_N_confirmations()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");

        await h.PayAsync(invoice, "10");
        var first = await h.PassAsync();
        Assert.Equal(1, first.NewTransfers);
        Assert.Equal(InvoiceStatus.Confirming, (await h.ReloadAsync(invoice.Id)).Status);

        await h.MineAsync();
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Confirming, (await h.ReloadAsync(invoice.Id)).Status); // 2 of 3

        await h.MineAsync();
        var third = await h.PassAsync();
        Assert.Equal(1, third.Confirmed);

        var paid = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal(UsdcAmount.Parse("10"), paid.ConfirmedTotal);
        var transfer = Assert.Single(paid.Transfers);
        Assert.Equal(TransferState.Confirmed, transfer.State);
        Assert.Equal(["invoice.confirming", "invoice.paid"], await h.EventTypesAsync(invoice.Id));

        var checkpoint = await h.Checkpoints.GetAsync();
        Assert.Equal(await chain.Anvil.GetBlockNumberAsync(), checkpoint!.LastScannedBlock);
        Assert.Equal(0, h.State.LagBlocks);
    }

    [Fact]
    public async Task Underpayment_is_reported_and_a_top_up_completes_the_invoice()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");

        await h.PayAsync(invoice, "4");
        await h.MineAsync(2);
        await h.PassAsync();
        var under = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Underpaid, under.Status);
        Assert.Equal(UsdcAmount.Parse("6"), under.RemainingAmount);

        await h.PayAsync(invoice, "6");
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Confirming, (await h.ReloadAsync(invoice.Id)).Status);
        await h.MineAsync(2);
        await h.PassAsync();

        Assert.Equal(InvoiceStatus.Paid, (await h.ReloadAsync(invoice.Id)).Status);
        Assert.Equal(
            ["invoice.confirming", "invoice.underpaid", "invoice.confirming", "invoice.paid"],
            await h.EventTypesAsync(invoice.Id));
    }

    [Fact]
    public async Task Overpayment_ends_overpaid_with_the_excess_visible()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");

        await h.PayAsync(invoice, "12.5");
        await h.MineAsync(2);
        await h.PassAsync();

        var over = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Overpaid, over.Status);
        Assert.Equal(UsdcAmount.Parse("2.5"), over.ExcessAmount);
    }

    [Fact]
    public async Task Split_payment_in_two_transfers_sums_up_to_paid()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");

        await h.PayAsync(invoice, "3.333333");
        await h.PayAsync(invoice, "6.666667");
        var pass = await h.PassAsync();
        Assert.Equal(2, pass.NewTransfers);
        Assert.Equal(InvoiceStatus.Confirming, (await h.ReloadAsync(invoice.Id)).Status);

        await h.MineAsync(2);
        await h.PassAsync();

        var paid = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal(2, paid.Transfers.Count);
        Assert.Equal(UsdcAmount.Parse("10"), paid.ConfirmedTotal);
    }

    [Fact]
    public async Task Expiry_waits_for_the_scanner_and_a_late_payment_ends_paid_late()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10", TimeSpan.FromMinutes(1));

        // The deadline passes on the service clock, but the scanner has not run yet in this process:
        // an on-time payment might still be in unscanned blocks, so nothing is expired.
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await h.Expiry.RunOnceAsync());
        Assert.Equal(InvoiceStatus.Pending, (await h.ReloadAsync(invoice.Id)).Status);

        await chain.Anvil.WarpAsync(TimeSpan.FromMinutes(2)); // chain time past the deadline too
        await h.PassAsync();
        Assert.Equal(1, await h.Expiry.RunOnceAsync());
        Assert.Equal(InvoiceStatus.Expired, (await h.ReloadAsync(invoice.Id)).Status);

        // Paid after the deadline (block timestamp >= expires_at): seen while expired, paid_late once confirmed.
        await h.PayAsync(invoice, "10");
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Expired, (await h.ReloadAsync(invoice.Id)).Status);
        await h.MineAsync(2);
        await h.PassAsync();

        var late = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.PaidLate, late.Status);
        Assert.False(Assert.Single(late.Transfers).IsOnTime);
        Assert.Equal(["invoice.expired", "invoice.paid_late"], await h.EventTypesAsync(invoice.Id));
    }

    [Fact]
    public async Task Payment_on_time_but_scanned_after_the_deadline_still_counts_as_paid()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10", TimeSpan.FromMinutes(1));

        await h.PayAsync(invoice, "10");                    // block timestamp before the deadline
        await chain.Anvil.WarpAsync(TimeSpan.FromMinutes(5)); // ...but the service only looks much later
        await h.MineAsync();
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.PassAsync();
        await h.Expiry.RunOnceAsync();

        var paid = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.True(Assert.Single(paid.Transfers).IsOnTime);
    }

    [Fact]
    public async Task Restart_in_the_middle_of_a_payment_does_not_count_it_twice()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");

        await h.PayAsync(invoice, "10");
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Confirming, (await h.ReloadAsync(invoice.Id)).Status);

        // Restart, and force the new instance to re-read the payment's block from scratch (worst case: checkpoint lost).
        await h.RestartAsync();
        var genesis = await chain.Anvil.GetBlockAsync(0);
        await h.Checkpoints.SaveAsync(0, genesis!.Hash);
        await h.MineAsync(2);
        var pass = await h.PassAsync();
        Assert.Equal(0, pass.NewTransfers);                 // seen again, recognised as the same (tx hash, log index)
        await h.PassAsync();                                 // and once more for good measure

        var paid = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal(UsdcAmount.Parse("10"), paid.ConfirmedTotal);
        Assert.Equal(1, await h.ObservedTransferRowsAsync(invoice.Id));
        Assert.Equal(["invoice.confirming", "invoice.paid"], await h.EventTypesAsync(invoice.Id));
    }

    [Fact]
    public async Task Payment_made_while_the_service_is_down_is_found_after_restart_in_chunks()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain, maxBlockRange: 2);
        var invoice = await h.CreateInvoiceAsync("10");
        await h.PassAsync();
        var before = (await h.Checkpoints.GetAsync())!.LastScannedBlock;

        await h.RestartAsync(); // "down": no passes while the payment happens and 10 more blocks are mined
        await h.PayAsync(invoice, "10");
        await h.MineAsync(10);

        var pass = await h.PassAsync();
        var head = await chain.Anvil.GetBlockNumberAsync();
        Assert.Equal(before + 1, pass.FromBlock);
        Assert.Equal(head, pass.ToBlock);
        Assert.Equal(1, pass.NewTransfers);
        Assert.Equal(head, (await h.Checkpoints.GetAsync())!.LastScannedBlock);

        var paid = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, paid.Status); // already N deep: recorded and confirmed in one transaction
        Assert.Equal(["invoice.confirming", "invoice.paid"], await h.EventTypesAsync(invoice.Id));
    }

    [Fact]
    public async Task Forced_reorg_rolls_back_a_confirming_payment_and_never_produces_paid()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");

        await h.PayAsync(invoice, "10");
        await h.MineAsync(); // 2 of 3 confirmations
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Confirming, (await h.ReloadAsync(invoice.Id)).Status);

        await chain.Anvil.ReorgAsync(2); // both blocks replaced; the payment is gone from the canonical chain
        var pass = await h.PassAsync();
        Assert.True(pass.ReorgDetected);
        Assert.Equal(1, pass.Reverted);

        var reverted = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Pending, reverted.Status);
        Assert.Equal(TransferState.Reverted, Assert.Single(reverted.Transfers).State);

        await h.MineAsync(5); // far beyond N: still nothing to confirm
        await h.PassAsync();
        await h.PassAsync();

        var after = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Pending, after.Status);
        Assert.Equal(UsdcAmount.Zero, after.ConfirmedTotal);
        var events = await h.EventTypesAsync(invoice.Id);
        Assert.Equal(["invoice.confirming", "payment.reverted", "invoice.pending"], events);
        Assert.DoesNotContain("invoice.paid", events);
    }

    [Fact]
    public async Task Reorg_of_part_of_a_split_payment_moves_the_invoice_back_to_underpaid()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");

        await h.PayAsync(invoice, "4");
        await h.MineAsync(2);
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Underpaid, (await h.ReloadAsync(invoice.Id)).Status);

        await h.PayAsync(invoice, "6");
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Confirming, (await h.ReloadAsync(invoice.Id)).Status);

        await chain.Anvil.ReorgAsync(1); // only the top-up's block
        await h.PassAsync();
        await h.MineAsync(3);
        await h.PassAsync();

        var after = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Underpaid, after.Status);
        Assert.Equal(UsdcAmount.Parse("4"), after.ConfirmedTotal);
        Assert.DoesNotContain("invoice.paid", await h.EventTypesAsync(invoice.Id));
    }

    [Fact]
    public async Task Transfer_to_a_paid_invoice_is_recorded_once_as_unmatched()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");
        await h.PayAsync(invoice, "10");
        await h.MineAsync(2);
        await h.PassAsync();
        Assert.Equal(InvoiceStatus.Paid, (await h.ReloadAsync(invoice.Id)).Status);

        var txHash = await h.PayAsync(invoice, "5");
        var pass = await h.PassAsync();
        Assert.Equal(1, pass.Unmatched);

        // Re-scanning the same blocks does not add it again (FR-8 for unmatched payments too).
        await h.Checkpoints.SaveAsync(0, (await chain.Anvil.GetBlockAsync(0))!.Hash);
        await h.PassAsync();

        var row = Assert.Single(await h.Unmatched.ListAsync(null, 10));
        Assert.Equal(txHash.ToLowerInvariant(), row.TxHash);
        Assert.Equal(UnmatchedReason.InvoiceClosed, row.Reason);
        Assert.Equal(invoice.Id, row.InvoiceId);
        Assert.Equal(UsdcAmount.Parse("5"), row.Amount);

        var stillPaid = await h.ReloadAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, stillPaid.Status);
        Assert.Single(stillPaid.Transfers);
    }

    [Fact]
    public async Task Transfers_to_addresses_that_are_not_deposit_addresses_are_ignored()
    {
        await using var h = await ScannerHarness.CreateAsync(postgres, chain);
        var invoice = await h.CreateInvoiceAsync("10");
        await chain.Usdc.TransferAsync("0x00000000000000000000000000000000000000ff", UsdcAmount.Parse("1"));

        var pass = await h.PassAsync();

        Assert.Equal(0, pass.NewTransfers + pass.Unmatched);
        Assert.Equal(InvoiceStatus.Pending, (await h.ReloadAsync(invoice.Id)).Status);
    }
}
