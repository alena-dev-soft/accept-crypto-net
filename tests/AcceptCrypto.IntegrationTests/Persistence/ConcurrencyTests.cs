using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AcceptCrypto.IntegrationTests.Persistence;

/// <summary>NFR-3: parallel processing of one invoice causes no status race (optimistic concurrency + retry).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConcurrencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_stale_writer_conflicts_even_when_only_transfers_changed()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync("10");
        var t1 = store.Transfer(invoice.DepositAddress, "4");
        var t2 = store.Transfer(invoice.DepositAddress, "6");
        await store.RecordAsync(invoice.Id, t1); // pending -> confirming

        await using var a = store.CreateDbContext();
        await using var b = store.CreateDbContext();
        var invoiceA = await a.Invoices.SingleAsync(i => i.Id == invoice.Id);
        var invoiceB = await b.Invoices.SingleAsync(i => i.Id == invoice.Id);

        // A records a second transfer: status stays "confirming", only a child row is added.
        Assert.Empty(invoiceA.RecordTransfer(t2, store.Now).Events);
        await a.SaveChangesAsync();

        // B confirms t1 based on the stale state (it does not know t2): must not commit.
        invoiceB.ConfirmTransfer(t1.Id, store.Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());

        Assert.Equal(2, await store.ScalarAsync("SELECT version FROM invoices"));
    }

    [Fact]
    public async Task Parallel_confirmations_settle_to_the_correct_final_status()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync("10");
        var parts = new[] { "1", "2", "3", "4" }.Select(a => store.Transfer(invoice.DepositAddress, a)).ToArray();
        foreach (var t in parts)
        {
            await store.RecordAsync(invoice.Id, t);
        }

        // Without the version check each worker would see the others' transfers unconfirmed and the invoice
        // would stay "confirming" forever (or "paid" would be emitted twice).
        await Task.WhenAll(parts.Select(t => Task.Run(() => store.ConfirmAsync(invoice.Id, t.Id))));

        var loaded = await store.Invoices.GetAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, loaded!.Status);
        Assert.Equal(UsdcAmount.Parse("10"), loaded.ConfirmedTotal);
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM outbox_events WHERE type = 'invoice.paid'"));
        Assert.Equal(4, await store.ScalarAsync("SELECT count(*) FROM observed_transfers WHERE state = 'Confirmed'"));
    }

    [Fact]
    public async Task Racing_cancel_and_payment_never_produce_an_impossible_history()
    {
        await using var store = await postgres.CreateStoreAsync();
        for (var round = 0; round < 10; round++)
        {
            var invoice = await store.CreateInvoiceAsync("10");
            var transfer = store.Transfer(invoice.DepositAddress, "10");

            var cancel = Task.Run(() => store.Invoices.UpdateAsync(invoice.Id, i =>
                new InvoiceMutation<bool>(true, i.Status == InvoiceStatus.Pending ? i.Cancel(store.Now) : [])));
            var pay = Task.Run(() => store.RecordAsync(invoice.Id, transfer));
            await Task.WhenAll(cancel, pay);

            var loaded = await store.Invoices.GetAsync(invoice.Id);
            var events = (await store.Outbox.ListByInvoiceAsync(invoice.Id)).Select(e => e.Type).ToArray();

            // Exactly one of the two won; the loser saw the winner's state.
            if (loaded!.Status == InvoiceStatus.Cancelled)
            {
                Assert.Equal([InvoiceEventTypes.Cancelled], events);
                Assert.Empty(loaded.Transfers);
                Assert.Equal(TransferRecordOutcome.InvoiceClosed, (await pay)!.Result);
            }
            else
            {
                Assert.Equal(InvoiceStatus.Confirming, loaded.Status);
                Assert.Equal([InvoiceEventTypes.Confirming], events);
            }
        }
    }
}
