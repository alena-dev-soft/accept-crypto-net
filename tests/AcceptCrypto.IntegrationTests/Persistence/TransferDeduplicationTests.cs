using System.Numerics;
using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Persistence;
using Npgsql;

namespace AcceptCrypto.IntegrationTests.Persistence;

/// <summary>FR-8: the same (chain_id, tx_hash, log_index) is never stored or counted twice.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TransferDeduplicationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Recording_the_same_transfer_again_is_a_duplicate_and_inserts_nothing()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync();
        var transfer = store.Transfer(invoice.DepositAddress, "10");

        var first = await store.RecordAsync(invoice.Id, transfer);
        var again = await store.RecordAsync(invoice.Id, transfer); // e.g. the scanner re-reads the block after a restart

        Assert.Equal(TransferRecordOutcome.Accepted, first!.Result);
        Assert.Equal(TransferRecordOutcome.Duplicate, again!.Result);
        Assert.Empty(again.Events);
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM observed_transfers"));
        Assert.Equal(TestStore.ChainId, await store.ScalarAsync("SELECT chain_id FROM observed_transfers"));
    }

    [Fact]
    public async Task Parallel_recording_of_the_same_transfer_stores_it_once()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync();
        var transfer = store.Transfer(invoice.DepositAddress, "10");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.RecordAsync(invoice.Id, transfer))));

        Assert.Single(results, r => r!.Result == TransferRecordOutcome.Accepted);
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM observed_transfers"));
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM outbox_events WHERE type = 'invoice.confirming'"));
        var loaded = await store.Invoices.GetAsync(invoice.Id);
        Assert.Equal(UsdcAmount.Parse("10"), loaded!.UnconfirmedTotal);
    }

    [Fact]
    public async Task The_database_rejects_a_second_row_with_the_same_key()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync();
        await store.RecordAsync(invoice.Id, store.Transfer(invoice.DepositAddress, "10"));

        // Bypass the domain: copy the row with a new surrogate ID.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => store.ScalarAsync(
            """
            INSERT INTO observed_transfers (invoice_id, chain_id, tx_hash, log_index, block_number, block_hash, block_timestamp,
                from_address, amount, is_on_time, state, seen_at)
            SELECT invoice_id, chain_id, tx_hash, log_index, block_number, block_hash, block_timestamp,
                from_address, amount, is_on_time, state, seen_at FROM observed_transfers
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Unmatched_payment_is_recorded_once_and_listed()
    {
        await using var store = await postgres.CreateStoreAsync();
        var huge = UsdcAmount.FromBaseUnits(UsdcAmount.MaxBaseUnits); // uint256 max: NFR-1 without precision loss
        var transfer = new ChainTransfer(TestStore.Hash(7), 3, 42, TestStore.Hash(8), store.Now, TestStore.PayerAddress, TestStore.Address(999), huge);

        Assert.True(await store.Unmatched.AddAsync(transfer, UnmatchedReason.UnknownAddress, null));
        Assert.False(await store.Unmatched.AddAsync(transfer, UnmatchedReason.UnknownAddress, null));

        var listed = Assert.Single(await store.Unmatched.ListAsync(null, 10));
        Assert.Equal(huge, listed.Amount);
        Assert.Equal(transfer.Id, new TransferId(listed.TxHash, listed.LogIndex));
        Assert.Equal(UnmatchedReason.UnknownAddress, listed.Reason);
        Assert.Equal(((BigInteger.One << 256) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), await store.TextAsync("SELECT amount::text FROM unmatched_payments"));
    }

    [Fact]
    public async Task Transfer_to_a_closed_invoice_goes_to_unmatched()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync();
        await store.Invoices.UpdateAsync(invoice.Id, i => new InvoiceMutation<bool>(true, i.Cancel(store.Now)));
        var late = store.Transfer(invoice.DepositAddress, "10");

        var result = await store.RecordAsync(invoice.Id, late);
        Assert.Equal(TransferRecordOutcome.InvoiceClosed, result!.Result);
        await store.Unmatched.AddAsync(late, UnmatchedReason.InvoiceClosed, invoice.Id);

        var listed = Assert.Single(await store.Unmatched.ListAsync(null, 10));
        Assert.Equal(invoice.Id, listed.InvoiceId);
        Assert.Equal(0, await store.ScalarAsync("SELECT count(*) FROM observed_transfers"));
    }
}
