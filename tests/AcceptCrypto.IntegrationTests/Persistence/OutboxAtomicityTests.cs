using System.Data.Common;
using System.Text.Json;
using AcceptCrypto.Domain;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AcceptCrypto.IntegrationTests.Persistence;

/// <summary>NFR-2: a status change and its outbox event commit together or not at all.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class OutboxAtomicityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Status_change_and_its_events_are_committed_together_in_causal_order()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync("10", TimeSpan.FromMinutes(30));
        var transfer = store.Transfer(invoice.DepositAddress, "4"); // on time
        await store.RecordAsync(invoice.Id, transfer);

        // Confirm after the deadline: one operation, three events: confirming -> underpaid -> expired.
        store.Clock.Advance(TimeSpan.FromHours(1));
        var update = await store.ConfirmAsync(invoice.Id, transfer.Id);

        Assert.Equal(InvoiceStatus.Expired, update!.Invoice.Status);
        var stored = await store.Outbox.ListByInvoiceAsync(invoice.Id);
        Assert.Equal(
            [InvoiceEventTypes.Confirming, InvoiceEventTypes.Underpaid, InvoiceEventTypes.Expired],
            stored.Select(e => e.Type));
        Assert.Equal(update.Events.Select(e => e.EventId), stored.Skip(1).Select(e => e.EventId));
        Assert.All(stored, e => Assert.Equal(OutboxEventStatus.Pending, e.Status));
        Assert.Equal("expired", await store.TextAsync("SELECT status FROM invoices"));

        using var payload = JsonDocument.Parse(stored[^1].Payload);
        var root = payload.RootElement;
        Assert.Equal(stored[^1].EventId, root.GetProperty("id").GetGuid());
        Assert.Equal("invoice.expired", root.GetProperty("type").GetString());
        Assert.Equal("underpaid", root.GetProperty("data").GetProperty("previous_status").GetString());
        var snapshot = root.GetProperty("data").GetProperty("invoice");
        Assert.Equal(invoice.Id, snapshot.GetProperty("id").GetGuid());
        Assert.Equal("expired", snapshot.GetProperty("status").GetString());
        Assert.Equal("4", snapshot.GetProperty("confirmed_total").GetString());
        Assert.Equal("10", snapshot.GetProperty("amount").GetString());
        Assert.Equal("6", snapshot.GetProperty("remaining_amount").GetString());
    }

    [Fact]
    public async Task If_the_outbox_insert_fails_the_status_change_is_rolled_back()
    {
        var failer = new FailOnOutboxInsert();
        await using var store = await postgres.CreateStoreAsync(failer);
        var invoice = await store.CreateInvoiceAsync("10");

        failer.Enabled = true;
        await Assert.ThrowsAnyAsync<Exception>(() => store.RecordAsync(invoice.Id, store.Transfer(invoice.DepositAddress, "10")));
        failer.Enabled = false;

        // No status without an event: nothing of the change is visible.
        var loaded = await store.Invoices.GetAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Pending, loaded!.Status);
        Assert.Empty(loaded.Transfers);
        Assert.Equal(0, await store.ScalarAsync("SELECT count(*) FROM observed_transfers"));
        Assert.Equal(0, await store.ScalarAsync("SELECT count(*) FROM outbox_events"));
        Assert.Equal(0, await store.ScalarAsync("SELECT version FROM invoices"));
    }

    [Fact]
    public async Task A_domain_error_saves_nothing()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync("10");
        await store.RecordAsync(invoice.Id, store.Transfer(invoice.DepositAddress, "1"));

        await Assert.ThrowsAsync<DomainException>(() =>
            store.Invoices.UpdateAsync(invoice.Id, i => new InvoiceMutation<bool>(true, i.Cancel(store.Now)))); // not pending

        Assert.Equal("confirming", await store.TextAsync("SELECT status FROM invoices"));
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM outbox_events"));
    }

    [Fact]
    public async Task Delivery_history_and_manual_resend_keep_the_event_id()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync("10");
        await store.RecordAsync(invoice.Id, store.Transfer(invoice.DepositAddress, "10"));
        var evt = Assert.Single(await store.Outbox.ListDueAsync(store.Now, 10));

        await store.Outbox.UpdateAsync(evt.EventId, e => e.RecordFailure(store.Now, 503, "Service Unavailable", TimeSpan.FromMilliseconds(12), store.Now.AddMinutes(1)));
        Assert.Empty(await store.Outbox.ListDueAsync(store.Now, 10));
        await store.Outbox.UpdateAsync(evt.EventId, e => e.RecordFailure(store.Now, null, "timeout", TimeSpan.FromSeconds(10), null));
        await store.Outbox.UpdateAsync(evt.EventId, e => e.Resend(store.Now));
        await store.Outbox.UpdateAsync(evt.EventId, e => e.RecordSuccess(store.Now, 200, TimeSpan.FromMilliseconds(5)));

        var history = Assert.Single(await store.Outbox.ListByInvoiceAsync(invoice.Id));
        Assert.Equal(evt.EventId, history.EventId);
        Assert.Equal(evt.Payload, history.Payload);
        Assert.Equal(OutboxEventStatus.Delivered, history.Status);
        Assert.Equal([503, null, 200], history.DeliveryAttempts.Select(a => a.HttpStatus));
    }

    [Fact]
    public async Task Concurrent_outbox_updates_are_detected()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync("10");
        await store.RecordAsync(invoice.Id, store.Transfer(invoice.DepositAddress, "10"));

        await using var a = store.CreateDbContext();
        await using var b = store.CreateDbContext();
        var ea = await a.OutboxEvents.SingleAsync();
        var eb = await b.OutboxEvents.SingleAsync();
        ea.RecordSuccess(store.Now, 200, TimeSpan.Zero);
        await a.SaveChangesAsync();
        eb.RecordSuccess(store.Now, 200, TimeSpan.Zero);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }

    /// <summary>Simulates a crash/DB error exactly when the outbox row is written.</summary>
    private sealed class FailOnOutboxInsert : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO outbox_events", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated failure while writing the outbox.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
