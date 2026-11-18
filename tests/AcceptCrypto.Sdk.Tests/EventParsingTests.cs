using System.Text.Json;
using AcceptCrypto.Sdk.Webhooks;

namespace AcceptCrypto.Sdk.Tests;

public class EventParsingTests
{
    [Fact]
    public void ParsesAStatusEventFromTheDocumentedPayload()
    {
        var webhookEvent = AcceptCryptoEvent.Parse(TestData.PaidEventJson);

        var paid = Assert.IsType<InvoiceStatusChangedEvent>(webhookEvent);
        Assert.Equal(TestData.EventId, paid.Id);
        Assert.Equal(AcceptCryptoEventTypes.InvoicePaid, paid.Type);
        Assert.Equal("2026-11-17", paid.ApiVersion);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 12, 3, 10, 512, TimeSpan.Zero), paid.CreatedAt);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal(InvoiceStatus.Confirming, paid.PreviousStatus);
        Assert.True(paid.Status.IsTerminal());
        Assert.Equal(Guid.Parse("0193a1b0-0000-7000-8000-000000000001"), paid.Invoice.Id);
        Assert.Equal("order-1001", paid.Invoice.ExternalOrderId);
        Assert.Equal(new UsdcAmount(10m), paid.Invoice.Amount);
        Assert.Equal(paid.Invoice.Amount, paid.Invoice.ConfirmedTotal);
        Assert.Equal(UsdcAmount.Zero, paid.Invoice.RemainingAmount);
        Assert.Equal("42", paid.Invoice.Metadata["customer"]);
        Assert.Equal(TestData.PaidEventJson, paid.RawJson);
    }

    [Theory]
    [InlineData("invoice.paid_late", "paid_late", "expired", InvoiceStatus.PaidLate, InvoiceStatus.Expired)]
    [InlineData("invoice.pending", "pending", "confirming", InvoiceStatus.Pending, InvoiceStatus.Confirming)]
    [InlineData("invoice.cancelled", "cancelled", "pending", InvoiceStatus.Cancelled, InvoiceStatus.Pending)]
    [InlineData("invoice.underpaid", "underpaid", "confirming", InvoiceStatus.Underpaid, InvoiceStatus.Confirming)]
    public void ParsesEveryStatusEvent(string type, string status, string previous, InvoiceStatus expected, InvoiceStatus expectedPrevious)
    {
        var json = TestData.PaidEventJson
            .Replace("\"invoice.paid\"", $"\"{type}\"", StringComparison.Ordinal)
            .Replace("\"status\":\"paid\"", $"\"status\":\"{status}\"", StringComparison.Ordinal)
            .Replace("\"previous_status\":\"confirming\"", $"\"previous_status\":\"{previous}\"", StringComparison.Ordinal);

        var changed = Assert.IsType<InvoiceStatusChangedEvent>(AcceptCryptoEvent.Parse(json));

        Assert.Equal(type, changed.Type);
        Assert.Equal(expected, changed.Status);
        Assert.Equal(expectedPrevious, changed.PreviousStatus);
    }

    [Fact]
    public void ParsesPaymentReverted()
    {
        var reverted = Assert.IsType<PaymentRevertedEvent>(AcceptCryptoEvent.Parse(TestData.RevertedEventJson));

        Assert.Equal(AcceptCryptoEventTypes.PaymentReverted, reverted.Type);
        Assert.Equal(InvoiceStatus.Confirming, reverted.Invoice.Status);
        Assert.Equal("0x01", reverted.Transfer.TxHash);
        Assert.Equal(UsdcAmount.FromBaseUnits(4_000_001), reverted.Transfer.Amount);
        Assert.Equal(77, reverted.Transfer.BlockNumber);
        Assert.Equal("0x02", reverted.Transfer.BlockHash);
    }

    [Fact]
    public void AnUnknownTypeIsKeptWithItsRawData()
    {
        var json = TestData.PaidEventJson
            .Replace("\"invoice.paid\"", "\"invoice.refunded\"", StringComparison.Ordinal)
            .Replace("\"previous_status\":\"confirming\"", "\"refund\":{\"amount\":\"1\"}", StringComparison.Ordinal)
            .Replace("\"api_version\":\"2026-11-17\"", "\"api_version\":\"2026-11-17\",\"new_field\":true", StringComparison.Ordinal);

        var unknown = Assert.IsType<UnknownAcceptCryptoEvent>(AcceptCryptoEvent.Parse(json));

        Assert.Equal("invoice.refunded", unknown.Type);
        Assert.Equal("1", unknown.Data.GetProperty("refund").GetProperty("amount").GetString());
        Assert.Equal(TestData.EventId, unknown.Id);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid","api_version":"x","data":{}}""")]
    [InlineData("""{"type":"invoice.paid","api_version":"x","data":{"invoice":{}}}""")]
    [InlineData("""{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a60","type":"payment.reverted","created_at":"2026-11-20T12:03:10+00:00","api_version":"x","data":{"invoice":{"id":"0193a1b0-0000-7000-8000-000000000001","external_order_id":"o","status":"pending","amount":"1","confirmed_total":"0","remaining_amount":"1","deposit_address":"0x1","created_at":"2026-11-20T12:00:00+00:00","expires_at":"2026-11-20T12:30:00+00:00","metadata":{}}}}""")]
    public void MalformedPayloadsAreRejected(string json)
    {
        var ex = Assert.Throws<AcceptCryptoWebhookException>(() => AcceptCryptoEvent.Parse(json));

        Assert.Equal(WebhookRejectionReason.MalformedPayload, ex.Reason);
    }

    [Fact]
    public void AmountsAsJsonNumbersAreRefused()
    {
        var json = TestData.PaidEventJson.Replace("\"amount\":\"10\"", "\"amount\":10", StringComparison.Ordinal);

        var ex = Assert.Throws<AcceptCryptoWebhookException>(() => AcceptCryptoEvent.Parse(json));

        Assert.IsType<JsonException>(ex.InnerException);
    }
}
