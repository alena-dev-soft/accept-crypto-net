using System.Net;
using System.Numerics;
using System.Text.Json;

namespace AcceptCrypto.Sdk.Tests;

public class ClientTests
{
    private static (AcceptCryptoClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond, Action<AcceptCryptoOptions>? configure = null)
    {
        var handler = new StubHandler(respond);
        var options = new AcceptCryptoOptions { BaseUrl = new Uri("https://pay.example.com/base"), ApiKey = TestData.ApiKey };
        configure?.Invoke(options);
        return (new AcceptCryptoClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task CreateSendsSnakeCaseBodyApiKeyAndIdempotencyKey()
    {
        var (client, handler) = Create(_ => TestData.Json(HttpStatusCode.Created, TestData.InvoiceJson()));

        var invoice = await client.CreateInvoiceAsync(
            new CreateInvoiceRequest
            {
                Amount = UsdcAmount.Parse("25.50"),
                ExternalOrderId = "order-42",
                Metadata = new Dictionary<string, string> { ["customer"] = "c-1" },
                ExpiresIn = TimeSpan.FromMinutes(30),
            },
            idempotencyKey: "order-42-attempt");

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://pay.example.com/base/v1/invoices", request.RequestUri!.ToString());
        Assert.Equal(TestData.ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
        Assert.Equal("order-42-attempt", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("25.5", json.RootElement.GetProperty("amount").GetString());
        Assert.Equal("order-42", json.RootElement.GetProperty("external_order_id").GetString());
        Assert.Equal("c-1", json.RootElement.GetProperty("metadata").GetProperty("customer").GetString());
        Assert.Equal(1800, json.RootElement.GetProperty("expires_in_seconds").GetInt64());

        Assert.False(invoice.IdempotentReplayed);
        Assert.Equal(TestData.InvoiceId, invoice.Id);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Equal(new UsdcAmount(25.5m), invoice.Amount);
        Assert.Equal("c-1", invoice.Metadata["customer"]);
        Assert.Equal("0x9858effd232b4033e47d90003d41ec34ecaeda94", invoice.Payment.Address);
        Assert.Equal(31337, invoice.Payment.ChainId);
        Assert.Equal(6, invoice.Payment.Decimals);
        Assert.Equal(new Uri("https://pay.example.com/pay/86CqUPAR7QCD6YAgyFmQV7vmI3LRUQMQQ9PMSND5xK4"), invoice.PaymentPageUrl);
    }

    [Fact]
    public async Task CreateOmitsOptionalFieldsAndGeneratesAnIdempotencyKey()
    {
        var (client, handler) = Create(_ => TestData.Json(HttpStatusCode.Created, TestData.InvoiceJson()));

        await client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(10m), ExternalOrderId = "o-1" });

        var (request, body) = Assert.Single(handler.Requests);
        Assert.True(Guid.TryParse(Assert.Single(request.Headers.GetValues("Idempotency-Key")), out _));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(["amount", "external_order_id"], json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task CreateWithoutKeyGenerationSendsNoIdempotencyKey()
    {
        var (client, handler) = Create(_ => TestData.Json(HttpStatusCode.Created, TestData.InvoiceJson()), o => o.GenerateIdempotencyKeys = false);

        await client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(10m), ExternalOrderId = "o-1" });

        Assert.False(handler.Requests[0].Request.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task CreateReportsAReplay()
    {
        var (client, _) = Create(_ =>
        {
            var response = TestData.Json(HttpStatusCode.OK, TestData.InvoiceJson());
            response.Headers.Add("Idempotent-Replayed", "true");
            return response;
        });

        var invoice = await client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(25.5m), ExternalOrderId = "order-42" }, "k");

        Assert.True(invoice.IdempotentReplayed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("ключ")]
    public async Task CreateRejectsAnInvalidIdempotencyKeyLocally(string key)
    {
        var (client, handler) = Create(_ => throw new InvalidOperationException("must not be called"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(1m), ExternalOrderId = "o" }, key));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateRejectsAFractionalLifetime()
    {
        var (client, _) = Create(_ => throw new InvalidOperationException("must not be called"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateInvoiceAsync(
            new CreateInvoiceRequest { Amount = new UsdcAmount(1m), ExternalOrderId = "o", ExpiresIn = TimeSpan.FromMilliseconds(1500) }));
    }

    [Fact]
    public async Task GetReturnsTransfersAndWebhookHistory()
    {
        var (client, handler) = Create(_ => TestData.Json(HttpStatusCode.OK, TestData.DetailsJson()));

        var details = await client.GetInvoiceAsync(TestData.InvoiceId);

        Assert.Equal($"https://pay.example.com/base/v1/invoices/{TestData.InvoiceId}", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal(InvoiceStatus.Underpaid, details.Status);
        var transfer = Assert.Single(details.Transfers);
        Assert.Equal(TransferState.Confirmed, transfer.State);
        Assert.Equal(new UsdcAmount(10m), transfer.Amount);
        Assert.Equal(5, transfer.Confirmations);
        Assert.True(transfer.OnTime);
        var webhookEvent = Assert.Single(details.Events);
        Assert.Equal(EventDeliveryStatus.Failed, webhookEvent.DeliveryStatus);
        Assert.Equal(TestData.EventId, webhookEvent.Id);
        Assert.Equal([503, null], webhookEvent.DeliveryAttempts.Select(a => a.HttpStatus));
    }

    [Fact]
    public async Task AnUnknownFutureStatusDoesNotBreakDeserialization()
    {
        var (client, _) = Create(_ => TestData.Json(HttpStatusCode.OK, TestData.InvoiceJson(status: "refunded")));

        var invoice = await client.GetInvoiceAsync(TestData.InvoiceId);

        Assert.Equal(InvoiceStatus.Unknown, invoice.Status);
    }

    [Fact]
    public async Task ListBuildsFiltersAndCursorQuery()
    {
        var (client, handler) = Create(_ => TestData.Json(HttpStatusCode.OK, $$"""{"data":[{{TestData.InvoiceJson()}}],"next_cursor":"abc+/="}"""));

        var page = await client.ListInvoicesAsync(new ListInvoicesQuery
        {
            Status = InvoiceStatus.PaidLate,
            CreatedFrom = new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.FromHours(2)),
            CreatedTo = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            Limit = 10,
            Cursor = "c/1",
        });

        Assert.Equal(
            "/base/v1/invoices?status=paid_late&created_from=2026-11-01T00%3A00%3A00.0000000%2B00%3A00"
            + "&created_to=2026-12-01T00%3A00%3A00.0000000%2B00%3A00&limit=10&cursor=c%2F1",
            handler.Requests[0].Request.RequestUri!.PathAndQuery);
        Assert.Single(page.Data);
        Assert.True(page.HasMore);
        Assert.Equal("abc+/=", page.NextCursor);
    }

    [Fact]
    public async Task ListAllFollowsTheCursorToTheLastPage()
    {
        var (client, handler) = Create(request => request.RequestUri!.Query.Contains("cursor=p2", StringComparison.Ordinal)
            ? TestData.Json(HttpStatusCode.OK, $$"""{"data":[{{TestData.InvoiceJson("paid")}}],"next_cursor":null}""")
            : TestData.Json(HttpStatusCode.OK, $$"""{"data":[{{TestData.InvoiceJson()}},{{TestData.InvoiceJson()}}],"next_cursor":"p2"}"""));

        var all = new List<Invoice>();
        await foreach (var invoice in client.ListAllInvoicesAsync(new ListInvoicesQuery { Status = InvoiceStatus.Pending }))
        {
            all.Add(invoice);
        }

        Assert.Equal([InvoiceStatus.Pending, InvoiceStatus.Pending, InvoiceStatus.Paid], all.Select(i => i.Status));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("status=pending", handler.Requests[1].Request.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnmatchedPaymentsKeepFullPrecisionEvenForHugeAmounts()
    {
        const string max = "115792089237316195423570985008687907853269984665640564039457584007913129.639935";
        var (client, handler) = Create(_ => TestData.Json(HttpStatusCode.OK, $$"""
            {"data":[{"id":7,"chain_id":31337,"tx_hash":"0x1","log_index":0,"block_number":5,"block_hash":"0x2",
              "block_timestamp":"2026-11-17T08:50:00+00:00","from":"0xa","to":"0xb","amount":"{{max}}",
              "reason":"invoice_closed","invoice_id":"{{TestData.InvoiceId}}","recorded_at":"2026-11-17T08:50:01+00:00"}],
             "next_cursor":null}
            """));

        var page = await client.ListUnmatchedPaymentsAsync(new ListUnmatchedPaymentsQuery { Limit = 5 });

        Assert.Equal("/base/v1/unmatched-payments?limit=5", handler.Requests[0].Request.RequestUri!.PathAndQuery);
        var payment = Assert.Single(page.Data);
        Assert.Equal(UnmatchedReason.InvoiceClosed, payment.Reason);
        Assert.Equal(TestData.InvoiceId, payment.InvoiceId);
        Assert.Equal((BigInteger.One << 256) - 1, payment.Amount.BaseUnits);
        Assert.Equal(max, payment.Amount.ToString());
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task CancelAndResendUseTheirRoutes()
    {
        var (client, handler) = Create(request => request.RequestUri!.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal)
            ? TestData.Json(HttpStatusCode.OK, TestData.InvoiceJson("cancelled"))
            : TestData.Json(HttpStatusCode.Accepted, TestData.EventInfoJson()));

        var cancelled = await client.CancelInvoiceAsync(TestData.InvoiceId);
        var resent = await client.ResendEventAsync(TestData.EventId);

        Assert.Equal(InvoiceStatus.Cancelled, cancelled.Status);
        Assert.Equal(EventDeliveryStatus.Pending, resent.DeliveryStatus);
        Assert.Equal(TestData.EventId, resent.Id);
        Assert.Equal($"/base/v1/invoices/{TestData.InvoiceId}/cancel", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal($"/base/v1/events/{TestData.EventId}/resend", handler.Requests[1].Request.RequestUri!.AbsolutePath);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Post, r.Request.Method));
    }

    [Fact]
    public async Task ValidationProblemBecomesATypedExceptionWithFieldErrors()
    {
        var (client, _) = Create(_ => TestData.Problem(HttpStatusCode.BadRequest, "validation_failed",
            """, "errors": { "amount": ["Must be a decimal string with at most 6 decimals."] }"""));

        var ex = await Assert.ThrowsAsync<AcceptCryptoValidationException>(() =>
            client.CreateInvoiceAsync(new CreateInvoiceRequest { Amount = new UsdcAmount(1m), ExternalOrderId = "o" }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal(AcceptCryptoErrorCodes.ValidationFailed, ex.ErrorCode);
        Assert.Equal("00-abc-00", ex.TraceId);
        Assert.Equal("D", ex.Detail);
        Assert.Equal(["Must be a decimal string with at most 6 decimals."], ex.ValidationErrors["amount"]);
        Assert.Contains("amount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProblemDetailsMapToTypedExceptions()
    {
        var responses = new Queue<HttpResponseMessage>([
            TestData.Problem(HttpStatusCode.Unauthorized, "api_key_invalid"),
            TestData.Problem(HttpStatusCode.NotFound, "invoice_not_found"),
            TestData.Problem(HttpStatusCode.Conflict, "invoice_not_cancellable", """, "current_status": "paid_late" """),
            TestData.Problem(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused", $$""", "invoice_id": "{{TestData.InvoiceId}}" """),
            TestData.Problem(HttpStatusCode.InternalServerError, "internal_error"),
        ]);
        var (client, _) = Create(_ => responses.Dequeue());
        var request = new CreateInvoiceRequest { Amount = new UsdcAmount(1m), ExternalOrderId = "o" };

        var auth = await Assert.ThrowsAsync<AcceptCryptoAuthenticationException>(() => client.GetInvoiceAsync(TestData.InvoiceId));
        var notFound = await Assert.ThrowsAsync<AcceptCryptoNotFoundException>(() => client.GetInvoiceAsync(TestData.InvoiceId));
        var conflict = await Assert.ThrowsAsync<AcceptCryptoConflictException>(() => client.CancelInvoiceAsync(TestData.InvoiceId));
        var reused = await Assert.ThrowsAsync<AcceptCryptoIdempotencyKeyReusedException>(() => client.CreateInvoiceAsync(request, "k"));
        var server = await Assert.ThrowsAsync<AcceptCryptoApiException>(() => client.ResendEventAsync(TestData.EventId));

        Assert.Equal(AcceptCryptoErrorCodes.ApiKeyInvalid, auth.ErrorCode);
        Assert.Equal(AcceptCryptoErrorCodes.InvoiceNotFound, notFound.ErrorCode);
        Assert.Equal(InvoiceStatus.PaidLate, conflict.CurrentStatus);
        Assert.Equal(TestData.InvoiceId, reused.InvoiceId);
        Assert.Equal(HttpStatusCode.InternalServerError, server.StatusCode);
        Assert.Equal("paid_late", conflict.Extensions["current_status"].GetString());
    }

    [Fact]
    public async Task NonProblemErrorStillBecomesAnApiException()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });

        var ex = await Assert.ThrowsAsync<AcceptCryptoApiException>(() => client.GetInvoiceAsync(TestData.InvoiceId));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public async Task TransportFailuresBecomeConnectionExceptions()
    {
        var (unreachable, _) = Create(_ => throw new HttpRequestException("Connection refused"));
        var (slow, _) = Create(_ => throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<AcceptCryptoConnectionException>(() => unreachable.GetInvoiceAsync(TestData.InvoiceId));
        await Assert.ThrowsAsync<AcceptCryptoConnectionException>(() => slow.GetInvoiceAsync(TestData.InvoiceId));
    }

    [Fact]
    public async Task CallerCancellationIsNotWrapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (client, _) = Create(_ => TestData.Json(HttpStatusCode.OK, TestData.InvoiceJson()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInvoiceAsync(TestData.InvoiceId, cts.Token));
    }

    [Fact]
    public async Task AnUnexpectedPayloadBecomesAnAcceptCryptoException()
    {
        var (client, _) = Create(_ => TestData.Json(HttpStatusCode.OK, """{"id":"x"}"""));

        var ex = await Assert.ThrowsAsync<AcceptCryptoException>(() => client.GetInvoiceAsync(TestData.InvoiceId));

        Assert.IsType<JsonException>(ex.InnerException);
    }

    [Fact]
    public async Task MissingApiKeyFailsWithAClearMessage()
    {
        var (client, _) = Create(_ => TestData.Json(HttpStatusCode.OK, TestData.InvoiceJson()), o => o.ApiKey = null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInvoiceAsync(TestData.InvoiceId));

        Assert.Contains("ApiKey", ex.Message, StringComparison.Ordinal);
    }
}
