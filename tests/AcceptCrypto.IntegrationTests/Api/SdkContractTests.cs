using System.Globalization;
using System.Text;
using AcceptCrypto.IntegrationTests.Persistence;
using AcceptCrypto.IntegrationTests.Webhooks;
using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.Extensions.Time.Testing;

namespace AcceptCrypto.IntegrationTests.Api;

/// <summary>
/// The SDK (FR-24/25/26/27) against the real service: the typed client talks to the real merchant API on a real
/// PostgreSQL, and webhooks sent by the real dispatcher are verified and parsed by the SDK. Proves the wire contract
/// (snake_case, amounts as strings, enums, Problem Details, signature format) matches on both sides.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SdkContractTests(PostgresFixture postgres)
{
    private static AcceptCryptoClient Client(ApiHarness api, string apiKey = TestMerchant.ApiKey) =>
        new(new HttpClient(api.Factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") },
            new AcceptCryptoOptions { ApiKey = apiKey });

    [Fact]
    public async Task The_typed_client_covers_every_merchant_operation_against_the_real_service()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var client = Client(api);
        var request = new CreateInvoiceRequest
        {
            Amount = UsdcAmount.Parse("25.50"),
            ExternalOrderId = "order-sdk-1",
            Metadata = new Dictionary<string, string> { ["customer"] = "c-1" },
            ExpiresIn = TimeSpan.FromMinutes(20),
        };

        var created = await client.CreateInvoiceAsync(request, "sdk-key-1");
        var replayed = await client.CreateInvoiceAsync(request, "sdk-key-1");

        Assert.False(created.IdempotentReplayed);
        Assert.True(replayed.IdempotentReplayed);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(Sdk.InvoiceStatus.Pending, created.Status);
        Assert.Equal(new UsdcAmount(25.5m), created.Amount);
        Assert.Equal(created.CreatedAt.AddMinutes(20), created.ExpiresAt);
        Assert.Equal("c-1", created.Metadata["customer"]);
        Assert.StartsWith("https://pay.example/pay/", created.PaymentPageUrl.ToString(), StringComparison.Ordinal);
        Assert.Equal(created.Amount, created.Payment.Amount);

        await api.PayAsync(created.Id, "10", confirm: true);
        var details = await client.GetInvoiceAsync(created.Id);
        Assert.Equal(Sdk.InvoiceStatus.Underpaid, details.Status);
        Assert.Equal(new UsdcAmount(15.5m), details.RemainingAmount);
        var transfer = Assert.Single(details.Transfers);
        Assert.Equal(TransferState.Confirmed, transfer.State);
        Assert.Equal(new UsdcAmount(10m), transfer.Amount);
        Assert.Equal(["invoice.confirming", "invoice.underpaid"], details.Events.Select(e => e.Type));
        Assert.All(details.Events, e => Assert.Equal(EventDeliveryStatus.Pending, e.DeliveryStatus));

        var resent = await client.ResendEventAsync(details.Events[0].Id);
        Assert.Equal(details.Events[0].Id, resent.Id);

        var other = await client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(1m), ExternalOrderId = "order-sdk-2" });
        var underpaid = await client.ListInvoicesAsync(new ListInvoicesQuery { Status = Sdk.InvoiceStatus.Underpaid });
        Assert.Equal([created.Id], underpaid.Data.Select(i => i.Id));
        var all = new List<Invoice>();
        await foreach (var invoice in client.ListAllInvoicesAsync(new ListInvoicesQuery { Limit = 1, CreatedFrom = ApiHarness.Start.AddDays(-1) }))
        {
            all.Add(invoice);
        }

        Assert.Equal([other.Id, created.Id], all.Select(i => i.Id));

        var cancelled = await client.CancelInvoiceAsync(other.Id);
        Assert.Equal(Sdk.InvoiceStatus.Cancelled, cancelled.Status);
        var conflict = await Assert.ThrowsAsync<AcceptCryptoConflictException>(() => client.CancelInvoiceAsync(created.Id));
        Assert.Equal(AcceptCryptoErrorCodes.InvoiceNotCancellable, conflict.ErrorCode);
        Assert.Equal(Sdk.InvoiceStatus.Underpaid, conflict.CurrentStatus);
        Assert.False(string.IsNullOrEmpty(conflict.TraceId));
    }

    [Fact]
    public async Task Service_errors_surface_as_typed_exceptions()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var client = Client(api);

        var invalid = await Assert.ThrowsAsync<AcceptCryptoValidationException>(() =>
            client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = UsdcAmount.Parse("0.000001"), ExternalOrderId = "o" }));
        Assert.Equal(AcceptCryptoErrorCodes.ValidationFailed, invalid.ErrorCode);
        Assert.True(invalid.ValidationErrors.ContainsKey("amount"));

        await client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(5m), ExternalOrderId = "o-1" }, "reused");
        var reused = await Assert.ThrowsAsync<AcceptCryptoIdempotencyKeyReusedException>(() =>
            client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(6m), ExternalOrderId = "o-1" }, "reused"));
        Assert.NotNull(reused.InvoiceId);

        var notFound = await Assert.ThrowsAsync<AcceptCryptoNotFoundException>(() => client.GetInvoiceAsync(Guid.NewGuid()));
        Assert.Equal(AcceptCryptoErrorCodes.InvoiceNotFound, notFound.ErrorCode);
        var noEvent = await Assert.ThrowsAsync<AcceptCryptoNotFoundException>(() => client.ResendEventAsync(Guid.NewGuid()));
        Assert.Equal(AcceptCryptoErrorCodes.EventNotFound, noEvent.ErrorCode);

        var unauthorized = await Assert.ThrowsAsync<AcceptCryptoAuthenticationException>(() =>
            Client(api, "wrong_key_dddddddddddddddddddddddddddddddd").GetInvoiceAsync(Guid.NewGuid()));
        Assert.Equal(AcceptCryptoErrorCodes.ApiKeyInvalid, unauthorized.ErrorCode);
    }

    [Fact]
    public async Task Unmatched_payments_are_listed_by_the_client()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        for (var n = 1; n <= 3; n++)
        {
            var transfer = new Domain.Chain.ChainTransfer(
                TestStore.Hash(n), n, 10 + n, TestStore.Hash(500 + n), ApiHarness.Start, TestStore.PayerAddress, TestStore.Address(900 + n),
                Domain.Money.UsdcAmount.Parse($"{n}.5"));
            await api.Unmatched.AddAsync(transfer, AcceptCrypto.Infrastructure.Persistence.UnmatchedReason.UnknownAddress, null);
        }

        var all = new List<UnmatchedPayment>();
        await foreach (var payment in Client(api).ListAllUnmatchedPaymentsAsync(new ListUnmatchedPaymentsQuery { Limit = 2 }))
        {
            all.Add(payment);
        }

        Assert.Equal(["3.5", "2.5", "1.5"], all.Select(p => p.Amount.ToString()));
        Assert.All(all, p => Assert.Equal(Sdk.UnmatchedReason.UnknownAddress, p.Reason));
        Assert.All(all, p => Assert.Equal(TestStore.ChainId, p.ChainId));
    }

    [Fact]
    public async Task A_webhook_sent_by_the_dispatcher_during_rotation_is_verified_and_parsed_by_the_sdk()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?>
        {
            ["Merchant:PreviousWebhookSecret"] = DispatcherHarness.PreviousSecret,
        });
        var stored = await harness.CreateEventAsync();
        await harness.RunOnceAsync();
        var received = Assert.Single(receiver.Received);
        var body = Encoding.UTF8.GetBytes(received.Body);
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(received.Timestamp, CultureInfo.InvariantCulture));

        // A receiver that only knows the old secret, one that knows only the new one: both accept.
        foreach (var secret in new[] { DispatcherHarness.PreviousSecret, DispatcherHarness.Secret })
        {
            var verifier = new AcceptCryptoWebhookVerifier([secret], timeProvider: new FakeTimeProvider(sentAt.AddSeconds(5)));
            var parsed = Assert.IsType<InvoiceStatusChangedEvent>(verifier.VerifyAndParse(received.Timestamp, received.Signature, body));
            Assert.Equal(stored.EventId, parsed.Id);
            Assert.Equal(received.EventId, parsed.Id.ToString());
            Assert.Equal(AcceptCryptoEventTypes.InvoiceConfirming, parsed.Type);
            Assert.Equal(Sdk.InvoiceStatus.Confirming, parsed.Status);
            Assert.Equal(Sdk.InvoiceStatus.Pending, parsed.PreviousStatus);
            Assert.Equal(stored.InvoiceId, parsed.Invoice.Id);
            Assert.Equal(new UsdcAmount(10m), parsed.Invoice.Amount);
        }

        // Replayed ten minutes later, or signed with an unknown secret: rejected.
        var late = new AcceptCryptoWebhookVerifier([DispatcherHarness.Secret], timeProvider: new FakeTimeProvider(sentAt.AddMinutes(10)));
        Assert.Equal(WebhookRejectionReason.TimestampOutsideTolerance, late.Verify(received.Timestamp, received.Signature, body));
        var stranger = new AcceptCryptoWebhookVerifier(["some_other_secret_eeeeeeeeeeeeeeeeeeeeeeeee"], timeProvider: new FakeTimeProvider(sentAt));
        Assert.Equal(WebhookRejectionReason.SignatureMismatch, stranger.Verify(received.Timestamp, received.Signature, body));
    }
}
