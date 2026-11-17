using AcceptCrypto.Domain;
using AcceptCrypto.Infrastructure.Persistence;

namespace AcceptCrypto.IntegrationTests.Persistence;

/// <summary>FR-2: a repeated create request with the same idempotency key returns the original invoice.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class IdempotentCreationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Same_key_and_request_returns_the_original_invoice()
    {
        await using var store = await postgres.CreateStoreAsync();

        var first = await store.Invoices.CreateAsync("key-1", "fp-a", store.Factory(idempotencyKey: "key-1"));
        var second = await store.Invoices.CreateAsync("key-1", "fp-a", store.Factory(idempotencyKey: "key-1"));

        Assert.Equal(InvoiceCreateOutcome.Created, first.Outcome);
        Assert.Equal(InvoiceCreateOutcome.Replayed, second.Outcome);
        Assert.Equal(first.Invoice.Id, second.Invoice.Id);
        Assert.Equal(first.Invoice.DepositAddress, second.Invoice.DepositAddress);
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM invoices"));
    }

    [Fact]
    public async Task Same_key_with_a_different_request_is_reported_and_creates_nothing()
    {
        await using var store = await postgres.CreateStoreAsync();

        var first = await store.Invoices.CreateAsync("key-1", "fp-a", store.Factory(idempotencyKey: "key-1"));
        var second = await store.Invoices.CreateAsync("key-1", "fp-b", store.Factory("99", idempotencyKey: "key-1"));

        Assert.Equal(InvoiceCreateOutcome.KeyReusedWithDifferentRequest, second.Outcome);
        Assert.Equal(first.Invoice.Id, second.Invoice.Id);
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM invoices"));
    }

    [Fact]
    public async Task Parallel_requests_with_the_same_key_create_exactly_one_invoice()
    {
        await using var store = await postgres.CreateStoreAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => store.Invoices.CreateAsync("key-race", "fp", store.Factory(idempotencyKey: "key-race")))));

        Assert.Single(results, r => r.Outcome == InvoiceCreateOutcome.Created);
        Assert.Single(results.Select(r => r.Invoice.Id).Distinct());
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM invoices"));
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM idempotency_keys WHERE invoice_id IS NOT NULL"));
    }

    [Fact]
    public async Task Requests_without_a_key_get_distinct_derivation_indexes_and_addresses()
    {
        await using var store = await postgres.CreateStoreAsync();

        var invoices = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.CreateInvoiceAsync())));

        Assert.Equal(10, invoices.Select(i => i.DerivationIndex).Distinct().Count());
        Assert.Equal(10, invoices.Select(i => i.DepositAddress).Distinct().Count());
        Assert.Equal(10, await store.ScalarAsync("SELECT count(*) FROM invoices"));
    }

    [Fact]
    public async Task A_rejected_request_does_not_burn_the_key()
    {
        await using var store = await postgres.CreateStoreAsync();

        await Assert.ThrowsAsync<DomainException>(() =>
            store.Invoices.CreateAsync("key-1", "fp-bad", store.Factory("0.001", idempotencyKey: "key-1"))); // below min amount

        var retry = await store.Invoices.CreateAsync("key-1", "fp-good", store.Factory(idempotencyKey: "key-1"));

        Assert.Equal(InvoiceCreateOutcome.Created, retry.Outcome);
        Assert.Equal(1, await store.ScalarAsync("SELECT count(*) FROM idempotency_keys"));
    }

    [Fact]
    public async Task Created_invoice_round_trips_with_all_fields()
    {
        await using var store = await postgres.CreateStoreAsync();
        var created = (await store.Invoices.CreateAsync("key-1", "fp", store.Factory("123.456789", idempotencyKey: "key-1"))).Invoice;

        var loaded = await store.Invoices.GetAsync(created.Id);
        var byToken = await store.Invoices.GetByPublicTokenAsync(created.PublicToken);
        var byAddress = await store.Invoices.FindByDepositAddressAsync(created.DepositAddress.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal));

        Assert.NotNull(loaded);
        Assert.Equal(created.Amount, loaded.Amount);
        Assert.Equal(created.ExternalOrderId, loaded.ExternalOrderId);
        Assert.Equal(created.ExpiresAt, loaded.ExpiresAt);
        Assert.Equal(created.Status, loaded.Status);
        Assert.Equal("key-1", loaded.IdempotencyKey);
        Assert.Equal(created.Id, byToken?.Id);
        Assert.Equal(created.Id, byAddress?.Id);

        // NFR-1: stored as integer base units, no floating point anywhere.
        Assert.Equal("123456789", await store.TextAsync("SELECT amount::text FROM invoices"));
    }
}
