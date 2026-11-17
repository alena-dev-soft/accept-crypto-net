using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AcceptCrypto.IntegrationTests.Persistence;

[Collection(PostgresTestGroup.Name)]
public sealed class QueryAndCheckpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_are_fully_applied()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var db = store.CreateDbContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Invoice_list_is_cursor_paginated_newest_first_and_filtered()
    {
        await using var store = await postgres.CreateStoreAsync();
        var created = new List<Invoice>();
        for (var i = 0; i < 7; i++)
        {
            created.Add(await store.CreateInvoiceAsync());
        }

        await store.Invoices.UpdateAsync(created[2].Id, i => new InvoiceMutation<bool>(true, i.Cancel(store.Now)));

        var seen = new List<Guid>();
        InvoiceCursor? cursor = null;
        do
        {
            var page = await store.Invoices.ListAsync(new InvoiceListQuery(After: cursor, Limit: 3));
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
        }
        while (cursor is not null);

        Assert.Equal(created.Select(i => i.Id).Reverse(), seen);

        var cancelled = await store.Invoices.ListAsync(new InvoiceListQuery(Status: InvoiceStatus.Cancelled));
        Assert.Equal(created[2].Id, Assert.Single(cancelled.Items).Id);

        var fromThird = await store.Invoices.ListAsync(new InvoiceListQuery(CreatedFrom: created[3].CreatedAt));
        Assert.Equal(4, fromThird.Items.Count);
    }

    [Fact]
    public async Task Due_for_expiry_lists_only_open_invoices_past_their_deadline()
    {
        await using var store = await postgres.CreateStoreAsync();
        var shortLived = await store.CreateInvoiceAsync(lifetime: TimeSpan.FromMinutes(5));
        var cancelled = await store.CreateInvoiceAsync(lifetime: TimeSpan.FromMinutes(5));
        await store.Invoices.UpdateAsync(cancelled.Id, i => new InvoiceMutation<bool>(true, i.Cancel(store.Now)));
        await store.CreateInvoiceAsync(lifetime: TimeSpan.FromHours(2));

        store.Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal([shortLived.Id], await store.Invoices.ListDueForExpiryAsync(store.Now, 10));
    }

    [Fact]
    public async Task Scanner_checkpoint_is_upserted()
    {
        await using var store = await postgres.CreateStoreAsync();
        Assert.Null(await store.Checkpoints.GetAsync());

        await store.Checkpoints.SaveAsync(100, TestStore.Hash(100));
        await store.Checkpoints.SaveAsync(150, TestStore.Hash(150));

        var checkpoint = await store.Checkpoints.GetAsync();
        Assert.Equal(150, checkpoint!.LastScannedBlock);
        Assert.Equal(TestStore.Hash(150), checkpoint.LastScannedBlockHash);
        Assert.Equal(TestStore.ChainId, checkpoint.ChainId);
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM scanner_checkpoints"));
    }
}
