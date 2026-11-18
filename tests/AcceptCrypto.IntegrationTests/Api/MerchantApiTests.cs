using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AcceptCrypto.IntegrationTests.Api;

/// <summary>Merchant REST API against the real host and PostgreSQL (FR-1–FR-5, FR-15, FR-17–FR-21).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MerchantApiTests(PostgresFixture postgres)
{
    // ---------- Authentication (FR-17) ----------

    [Theory]
    [InlineData("GET", "/v1/invoices")]
    [InlineData("POST", "/v1/invoices")]
    [InlineData("GET", "/v1/invoices/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/v1/invoices/00000000-0000-0000-0000-000000000001/cancel")]
    [InlineData("GET", "/v1/unmatched-payments")]
    [InlineData("POST", "/v1/events/00000000-0000-0000-0000-000000000001/resend")]
    public async Task Every_merchant_endpoint_requires_the_api_key(string method, string path)
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        using var missing = await api.Anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });
        await ApiHarness.AssertProblemAsync(missing, HttpStatusCode.Unauthorized, "api_key_missing");
        Assert.Contains("X-Api-Key", missing.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        var wrong = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        wrong.Headers.Add("X-Api-Key", TestMerchant.ApiKey + "x");
        using var invalid = await api.Anonymous.SendAsync(wrong);
        await ApiHarness.AssertProblemAsync(invalid, HttpStatusCode.Unauthorized, "api_key_invalid");
    }

    [Fact]
    public async Task Health_and_openapi_are_public_and_the_document_describes_every_operation()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        Assert.Equal("Healthy", await api.Anonymous.GetStringAsync("/health"));
        using var ready = await api.Anonymous.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", (await ApiHarness.ReadJsonAsync(ready))["checks"]!["database"]!["status"]!.GetValue<string>());

        var doc = JsonNode.Parse(await api.Anonymous.GetStringAsync("/openapi/v1.json"))!;
        var paths = doc["paths"]!.AsObject();
        Assert.NotNull(paths["/v1/invoices"]!["post"]);
        Assert.NotNull(paths["/v1/invoices"]!["get"]);
        Assert.NotNull(paths["/v1/invoices/{id}"]!["get"]);
        Assert.NotNull(paths["/v1/invoices/{id}/cancel"]!["post"]);
        Assert.NotNull(paths["/v1/unmatched-payments"]!["get"]);
        Assert.NotNull(paths["/v1/events/{eventId}/resend"]!["post"]);
        Assert.Equal("apiKey", doc["components"]!["securitySchemes"]!["ApiKey"]!["type"]!.GetValue<string>());
        Assert.NotNull(paths["/v1/invoices"]!["post"]!["security"]);
        Assert.Contains("external_order_id", doc["components"]!["schemas"]!["CreateInvoiceRequest"]!["properties"]!.AsObject().Select(p => p.Key));

        using var ui = await api.Anonymous.GetAsync("/scalar/");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }

    // ---------- Create (FR-1, FR-4, FR-29 link) ----------

    [Fact]
    public async Task Create_returns_201_with_payment_details_and_payment_page_link()
    {
        await using var api = await ApiHarness.CreateAsync(postgres, new() { ["Invoices:DefaultLifetime"] = "00:20:00" });

        using var response = await api.CreateInvoiceAsync(new { amount = "25.50", external_order_id = "order-42", metadata = new { customer = "c-1" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiHarness.ReadJsonAsync(response);
        var id = body["id"]!.GetValue<Guid>();
        Assert.Equal($"/v1/invoices/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("pending", body["status"]!.GetValue<string>());
        Assert.Equal("25.5", body["amount"]!.GetValue<string>());
        Assert.Equal("USDC", body["currency"]!.GetValue<string>());
        Assert.Equal("order-42", body["external_order_id"]!.GetValue<string>());
        Assert.Equal("c-1", body["metadata"]!["customer"]!.GetValue<string>());
        Assert.Equal(ApiHarness.Start.AddMinutes(20), body["expires_at"]!.GetValue<DateTimeOffset>(), TimeSpan.FromSeconds(1));

        var stored = await api.Invoices.GetAsync(id);
        var payment = body["payment"]!;
        Assert.Equal(31337, payment["chain_id"]!.GetValue<long>());
        Assert.Equal("USDC", payment["token"]!.GetValue<string>());
        Assert.Equal(6, payment["decimals"]!.GetValue<int>());
        Assert.Equal("25.5", payment["amount"]!.GetValue<string>());
        Assert.Equal(stored!.DepositAddress, payment["address"]!.GetValue<string>());
        var deriver = api.Factory.Services.GetRequiredService<IDepositAddressDeriver>();
        Assert.Equal(deriver.DeriveAddress(stored.DerivationIndex), payment["address"]!.GetValue<string>(), ignoreCase: true);
        Assert.Null(payment["external_order_id"]);
        Assert.Null(payment["metadata"]);
        Assert.Equal($"https://pay.example/pay/{stored.PublicToken}", body["payment_page_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Same_idempotency_key_and_body_returns_the_original_invoice()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var request = new { amount = "10", external_order_id = "order-1", metadata = new { a = "1", b = "2" } };

        using var first = await api.CreateInvoiceAsync(request, "key-1");
        using var second = await api.CreateInvoiceAsync(new { amount = "10.000", external_order_id = "order-1", metadata = new { b = "2", a = "1" } }, "key-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await ApiHarness.ReadJsonAsync(first))["id"]!.GetValue<Guid>(), (await ApiHarness.ReadJsonAsync(second))["id"]!.GetValue<Guid>());
        Assert.Single((await api.Invoices.ListAsync(new InvoiceListQuery())).Items);
    }

    [Fact]
    public async Task Same_idempotency_key_with_a_different_body_is_rejected()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        using var first = await api.CreateInvoiceAsync(new { amount = "10", external_order_id = "order-1" }, "key-2");
        var id = (await ApiHarness.ReadJsonAsync(first))["id"]!.GetValue<Guid>();

        using var reused = await api.CreateInvoiceAsync(new { amount = "11", external_order_id = "order-1" }, "key-2");

        var problem = await ApiHarness.AssertProblemAsync(reused, HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(id, problem["invoice_id"]!.GetValue<Guid>());
        Assert.Single((await api.Invoices.ListAsync(new InvoiceListQuery())).Items);
    }

    [Fact]
    public async Task Parallel_requests_with_one_idempotency_key_create_one_invoice()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => api.CreateInvoiceAsync(new { amount = "5", external_order_id = "order-p" }, "key-parallel")));

        var ids = new HashSet<Guid>();
        foreach (var r in responses)
        {
            Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString());
            ids.Add((await ApiHarness.ReadJsonAsync(r))["id"]!.GetValue<Guid>());
            r.Dispose();
        }

        Assert.Single(ids);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single((await api.Invoices.ListAsync(new InvoiceListQuery())).Items);
    }

    [Theory]
    [InlineData("""{"external_order_id":"o"}""", "amount")]
    [InlineData("""{"amount":"","external_order_id":"o"}""", "amount")]
    [InlineData("""{"amount":"1.0000001","external_order_id":"o"}""", "amount")]
    [InlineData("""{"amount":"-1","external_order_id":"o"}""", "amount")]
    [InlineData("""{"amount":"1e3","external_order_id":"o"}""", "amount")]
    [InlineData("""{"amount":"0.001","external_order_id":"o"}""", "amount")]
    [InlineData("""{"amount":"1000000.01","external_order_id":"o"}""", "amount")]
    [InlineData("""{"amount":"10"}""", "external_order_id")]
    [InlineData("""{"amount":"10","external_order_id":"   "}""", "external_order_id")]
    [InlineData("""{"amount":"10","external_order_id":"o","expires_in_seconds":0}""", "expires_in_seconds")]
    [InlineData("""{"amount":"10","external_order_id":"o","expires_in_seconds":30}""", "expires_in_seconds")]
    [InlineData("""{"amount":"10","external_order_id":"o","expires_in_seconds":99999999}""", "expires_in_seconds")]
    [InlineData("""{"amount":"10","external_order_id":"o","metadata":{"":"x"}}""", "metadata")]
    public async Task Invalid_create_requests_return_validation_problems(string json, string field)
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        using var response = await api.Client.PostAsync("/v1/invoices", new StringContent(json, Encoding.UTF8, "application/json"));

        var problem = await ApiHarness.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.NotNull(problem["errors"]![field]);
        Assert.Empty((await api.Invoices.ListAsync(new InvoiceListQuery())).Items);
    }

    [Theory]
    [InlineData("""{"amount":10,"external_order_id":"o"}""")]
    [InlineData("""{"amount":"10",""")]
    [InlineData("""[]""")]
    [InlineData("")]
    public async Task Malformed_bodies_return_400_problem_details(string json)
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        using var response = await api.Client.PostAsync("/v1/invoices", new StringContent(json, Encoding.UTF8, "application/json"));

        await ApiHarness.AssertProblemAsync(response, HttpStatusCode.BadRequest, "malformed_request");
    }

    [Fact]
    public async Task Invalid_idempotency_key_is_rejected_and_a_failed_request_does_not_burn_the_key()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        using var badKey = await api.CreateInvoiceAsync(new { amount = "10", external_order_id = "o" }, new string('k', 256));
        var problem = await ApiHarness.AssertProblemAsync(badKey, HttpStatusCode.BadRequest, "validation_failed");
        Assert.NotNull(problem["errors"]!["Idempotency-Key"]);

        using var invalid = await api.CreateInvoiceAsync(new { amount = "0.0001", external_order_id = "o" }, "key-3");
        await ApiHarness.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
        using var valid = await api.CreateInvoiceAsync(new { amount = "10", external_order_id = "o" }, "key-3");
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
    }

    [Fact]
    public async Task Scripted_payer_reads_the_payment_details_from_the_merchant_api()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var body = await api.CreateInvoiceJsonAsync("12.34");
        var id = body["id"]!.GetValue<Guid>();

        using var http = api.Factory.CreateClient();
        var reference = AcceptCrypto.Payer.InvoiceReference.Parse(id.ToString(), http.BaseAddress!);
        var details = await new AcceptCrypto.Payer.PaymentDetailsClient(http).GetAsync(reference, TestMerchant.ApiKey, CancellationToken.None);

        Assert.Equal(body["payment"]!["address"]!.GetValue<string>(), details.Address);
        Assert.Equal(UsdcAmount.Parse("12.34"), details.Amount);
        Assert.Equal(31337, details.ChainId);
    }

    // ---------- Get (FR-19) ----------

    [Fact]
    public async Task Get_shows_transfers_and_webhook_history()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var id = (await api.CreateInvoiceJsonAsync("10"))["id"]!.GetValue<Guid>();
        var transfer = await api.PayAsync(id, "10", confirm: true);

        var body = JsonNode.Parse(await api.Client.GetStringAsync($"/v1/invoices/{id}"))!;

        Assert.Equal("paid", body["status"]!.GetValue<string>());
        Assert.Equal("10", body["confirmed_total"]!.GetValue<string>());
        var t = Assert.Single(body["transfers"]!.AsArray())!;
        Assert.Equal(transfer.Id.TxHash, t["tx_hash"]!.GetValue<string>());
        Assert.Equal("confirmed", t["state"]!.GetValue<string>());
        Assert.Equal("10", t["amount"]!.GetValue<string>());
        Assert.True(t["on_time"]!.GetValue<bool>());
        var events = body["events"]!.AsArray().Select(e => e!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(["invoice.confirming", "invoice.paid"], events);
        Assert.All(body["events"]!.AsArray(), e => Assert.Equal("pending", e!["delivery_status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Get_unknown_invoice_returns_404_problem()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        using var unknown = await api.Client.GetAsync($"/v1/invoices/{Guid.NewGuid()}");
        await ApiHarness.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "invoice_not_found");

        using var notAGuid = await api.Client.GetAsync("/v1/invoices/not-a-guid");
        await ApiHarness.AssertProblemAsync(notAGuid, HttpStatusCode.NotFound, "not_found");
    }

    // ---------- List (FR-18) ----------

    [Fact]
    public async Task List_is_cursor_paginated_newest_first_without_gaps_or_duplicates()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add((await api.CreateInvoiceJsonAsync())["id"]!.GetValue<Guid>());
            api.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var seen = new List<Guid>();
        var pages = 0;
        string? cursor = null;
        do
        {
            var url = "/v1/invoices?limit=2" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = JsonNode.Parse(await api.Client.GetStringAsync(url))!;
            seen.AddRange(page["data"]!.AsArray().Select(x => x!["id"]!.GetValue<Guid>()));
            cursor = page["next_cursor"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Reverse(created), seen);
    }

    [Fact]
    public async Task List_filters_by_status_and_creation_date()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var a = (await api.CreateInvoiceJsonAsync())["id"]!.GetValue<Guid>();
        api.Clock.Advance(TimeSpan.FromHours(1));
        var b = (await api.CreateInvoiceJsonAsync())["id"]!.GetValue<Guid>();
        api.Clock.Advance(TimeSpan.FromHours(1));
        var c = (await api.CreateInvoiceJsonAsync())["id"]!.GetValue<Guid>();
        using var _ = await api.Client.PostAsync($"/v1/invoices/{b}/cancel", null);

        async Task<List<Guid>> Ids(string query) =>
            JsonNode.Parse(await api.Client.GetStringAsync("/v1/invoices?" + query))!["data"]!.AsArray().Select(x => x!["id"]!.GetValue<Guid>()).ToList();

        Assert.Equal([b], await Ids("status=cancelled"));
        Assert.Equal([c, a], await Ids("status=pending"));
        Assert.Equal([b], await Ids("created_from=2026-11-20T12:30:00Z&created_to=2026-11-20T13:30:00Z"));
        Assert.Equal([c, b], await Ids("created_from=2026-11-20T12:30:00Z"));
        Assert.Equal([a], await Ids("created_to=2026-11-20T12:30:00%2B00:00"));
        Assert.Empty(await Ids("status=paid"));
    }

    [Theory]
    [InlineData("status=done", "status")]
    [InlineData("created_from=yesterday", "created_from")]
    [InlineData("created_from=2026-11-21T00:00:00Z&created_to=2026-11-20T00:00:00Z", "created_to")]
    [InlineData("cursor=not-a-cursor", "cursor")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("limit=ten", "limit")]
    public async Task Invalid_list_parameters_return_validation_problems(string query, string field)
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        using var response = await api.Client.GetAsync("/v1/invoices?" + query);

        var problem = await ApiHarness.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.NotNull(problem["errors"]![field]);
    }

    // ---------- Cancel (D6) ----------

    [Fact]
    public async Task Cancel_moves_a_pending_invoice_to_cancelled_once()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var id = (await api.CreateInvoiceJsonAsync())["id"]!.GetValue<Guid>();

        using var first = await api.Client.PostAsync($"/v1/invoices/{id}/cancel", null);
        using var again = await api.Client.PostAsync($"/v1/invoices/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("cancelled", (await ApiHarness.ReadJsonAsync(first))["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var events = await api.Outbox.ListByInvoiceAsync(id);
        Assert.Equal("invoice.cancelled", Assert.Single(events).Type);
    }

    [Fact]
    public async Task Cancel_is_refused_once_a_payment_was_seen()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var id = (await api.CreateInvoiceJsonAsync())["id"]!.GetValue<Guid>();
        await api.PayAsync(id, "4", confirm: false);

        using var response = await api.Client.PostAsync($"/v1/invoices/{id}/cancel", null);

        var problem = await ApiHarness.AssertProblemAsync(response, HttpStatusCode.Conflict, "invoice_not_cancellable");
        Assert.Equal("confirming", problem["current_status"]!.GetValue<string>());
        Assert.Equal(Domain.Invoices.InvoiceStatus.Confirming, (await api.Invoices.GetAsync(id))!.Status);

        using var unknown = await api.Client.PostAsync($"/v1/invoices/{Guid.NewGuid()}/cancel", null);
        await ApiHarness.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "invoice_not_found");
    }

    // ---------- Unmatched payments (FR-11a, S7) ----------

    [Fact]
    public async Task Unmatched_payments_are_listed_newest_first_with_a_cursor()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        for (var n = 1; n <= 3; n++)
        {
            var transfer = new ChainTransfer(
                TestStore.Hash(n), n, 10 + n, TestStore.Hash(500 + n), ApiHarness.Start, TestStore.PayerAddress, TestStore.Address(900 + n), UsdcAmount.Parse($"{n}.5"));
            await api.Unmatched.AddAsync(transfer, UnmatchedReason.UnknownAddress, null);
        }

        var page1 = JsonNode.Parse(await api.Client.GetStringAsync("/v1/unmatched-payments?limit=2"))!;
        var cursor = page1["next_cursor"]!.GetValue<string>();
        var page2 = JsonNode.Parse(await api.Client.GetStringAsync("/v1/unmatched-payments?limit=2&cursor=" + Uri.EscapeDataString(cursor)))!;

        var amounts = page1["data"]!.AsArray().Concat(page2["data"]!.AsArray()).Select(x => x!["amount"]!.GetValue<string>()).ToList();
        Assert.Equal(["3.5", "2.5", "1.5"], amounts);
        Assert.Null(page2["next_cursor"]);
        var first = page1["data"]![0]!;
        Assert.Equal("unknown_address", first["reason"]!.GetValue<string>());
        Assert.Equal(TestStore.Hash(3), first["tx_hash"]!.GetValue<string>());
        Assert.Equal(TestStore.Address(903), first["to"]!.GetValue<string>());

        using var bad = await api.Client.GetAsync("/v1/unmatched-payments?cursor=abc");
        await ApiHarness.AssertProblemAsync(bad, HttpStatusCode.BadRequest, "validation_failed");
    }

    // ---------- Resend (FR-15) ----------

    [Fact]
    public async Task Resend_requeues_the_event_with_its_original_id_and_payload()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var id = (await api.CreateInvoiceJsonAsync())["id"]!.GetValue<Guid>();
        using var cancel = await api.Client.PostAsync($"/v1/invoices/{id}/cancel", null);
        var original = Assert.Single(await api.Outbox.ListByInvoiceAsync(id));
        await api.Outbox.UpdateAsync(original.EventId, e => e.RecordSuccess(api.Clock.GetUtcNow(), 200, TimeSpan.FromMilliseconds(30)));

        api.Clock.Advance(TimeSpan.FromMinutes(5));
        using var response = await api.Client.PostAsync($"/v1/events/{original.EventId}/resend", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await ApiHarness.ReadJsonAsync(response);
        Assert.Equal(original.EventId, body["id"]!.GetValue<Guid>());
        Assert.Equal("pending", body["delivery_status"]!.GetValue<string>());
        Assert.Equal(0, body["attempts"]!.GetValue<int>());
        Assert.Single(body["delivery_attempts"]!.AsArray()); // history is kept
        var after = Assert.Single(await api.Outbox.ListByInvoiceAsync(id));
        Assert.Equal(original.EventId, after.EventId);
        Assert.Equal(original.Payload, after.Payload);
        Assert.Equal(OutboxEventStatus.Pending, after.Status);

        using var unknown = await api.Client.PostAsync($"/v1/events/{Guid.NewGuid()}/resend", null);
        await ApiHarness.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "event_not_found");
    }

    // ---------- Secrets (NFR-5) ----------

    [Fact]
    public async Task Secrets_never_appear_in_logs()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        await api.CreateInvoiceJsonAsync();
        var wrong = new HttpRequestMessage(HttpMethod.Get, "/v1/invoices");
        wrong.Headers.Add("X-Api-Key", "wrong_key_that_should_not_be_logged_123456");
        using var _ = await api.Anonymous.SendAsync(wrong);

        var all = string.Join("\n", api.Logs.Messages);
        Assert.Contains("Invoice", all, StringComparison.Ordinal); // logging works
        Assert.DoesNotContain(TestMerchant.ApiKey, all, StringComparison.Ordinal);
        Assert.DoesNotContain(TestMerchant.WebhookSecret, all, StringComparison.Ordinal);
        Assert.DoesNotContain(TestMerchant.PreviousWebhookSecret, all, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong_key_that_should_not_be_logged", all, StringComparison.Ordinal);
    }
}
