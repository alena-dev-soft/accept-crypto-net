using System.Text.Json;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.IntegrationTests.Persistence;

namespace AcceptCrypto.IntegrationTests.Webhooks;

/// <summary>The webhook body follows the stable schema documented in docs/webhooks.md and leaks no secrets.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class WebhookPayloadTests(PostgresFixture postgres)
{
    private static readonly string[] EnvelopeKeys = ["id", "type", "created_at", "api_version", "data"];

    private static readonly string[] InvoiceKeys =
    [
        "id", "external_order_id", "status", "amount", "confirmed_total", "remaining_amount",
        "deposit_address", "created_at", "expires_at", "metadata",
    ];

    [Fact]
    public async Task Status_change_and_payment_reverted_bodies_have_the_documented_fields_only()
    {
        await using var store = await postgres.CreateStoreAsync();
        var invoice = await store.CreateInvoiceAsync("10");
        var transfer = store.Transfer(invoice.DepositAddress, "2.5");
        await store.RecordAsync(invoice.Id, transfer);
        await store.Invoices.UpdateAsync(invoice.Id, i => new InvoiceMutation<bool>(true, i.RevertTransfer(transfer.Id, store.Now)));

        var events = await store.Outbox.ListByInvoiceAsync(invoice.Id);
        Assert.Equal(["invoice.confirming", "payment.reverted", "invoice.pending"], events.Select(e => e.Type));

        foreach (var evt in events)
        {
            using var doc = JsonDocument.Parse(evt.Payload);
            var root = doc.RootElement;
            Assert.Equal(EnvelopeKeys, Keys(root));
            Assert.Equal(evt.EventId, root.GetProperty("id").GetGuid());
            Assert.Equal(evt.Type, root.GetProperty("type").GetString());
            Assert.Equal(OutboxEventFactory.PayloadVersion, root.GetProperty("api_version").GetString());

            var snapshot = root.GetProperty("data").GetProperty("invoice");
            Assert.Equal(InvoiceKeys, Keys(snapshot));
            Assert.Equal(invoice.Id, snapshot.GetProperty("id").GetGuid());
            Assert.Equal(invoice.ExternalOrderId, snapshot.GetProperty("external_order_id").GetString());
            Assert.Equal("10", snapshot.GetProperty("amount").GetString());
            Assert.Equal(invoice.DepositAddress, snapshot.GetProperty("deposit_address").GetString());

            // No payment-page token, idempotency key or derivation index in a webhook.
            Assert.DoesNotContain(invoice.PublicToken, evt.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("token", evt.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("idempotency", evt.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("derivation", evt.Payload, StringComparison.OrdinalIgnoreCase);
        }

        using (var confirming = JsonDocument.Parse(events[0].Payload))
        {
            var data = confirming.RootElement.GetProperty("data");
            Assert.Equal(["invoice", "previous_status"], Keys(data));
            Assert.Equal("pending", data.GetProperty("previous_status").GetString());
            Assert.Equal("confirming", data.GetProperty("invoice").GetProperty("status").GetString());
            Assert.Equal("0", data.GetProperty("invoice").GetProperty("confirmed_total").GetString());
        }

        using (var reverted = JsonDocument.Parse(events[1].Payload))
        {
            var data = reverted.RootElement.GetProperty("data");
            Assert.Equal(["invoice", "transfer"], Keys(data));
            var t = data.GetProperty("transfer");
            Assert.Equal(["tx_hash", "log_index", "amount", "block_number", "block_hash"], Keys(t));
            Assert.Equal(transfer.Id.TxHash, t.GetProperty("tx_hash").GetString());
            Assert.Equal("2.5", t.GetProperty("amount").GetString());
        }
    }

    private static string[] Keys(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];
}
