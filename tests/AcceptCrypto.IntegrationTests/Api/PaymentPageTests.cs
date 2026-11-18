using System.Net;
using System.Text.Json.Nodes;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.IntegrationTests.Persistence;
using AcceptCrypto.Service.PaymentPage;
using Microsoft.Extensions.DependencyInjection;

namespace AcceptCrypto.IntegrationTests.Api;

/// <summary>The payer's public payment page and its status JSON (FR-4, FR-29, FR-30) against the real host and PostgreSQL.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PaymentPageTests(PostgresFixture postgres)
{
    private const string OrderId = "order-CONFIDENTIAL-7731";
    private const string MetaKey = "customer_email";
    private const string MetaValue = "alice.secret@example.com";
    private const string TokenAddress = "0x5FbDB2315678afecb367f032d93F642f64180aa3";

    /// <summary>The complete public contract: adding a field must be a deliberate decision (FR-29).</summary>
    private static readonly string[] PublicFields =
    [
        "status", "network", "chain_id", "token", "token_address", "decimals", "address", "amount", "paid_amount",
        "pending_amount", "remaining_amount", "excess_amount", "expires_at", "confirmations", "payment_uri",
        "accepting_payment", "server_time",
    ];

    [Fact]
    public async Task Page_qr_and_status_json_never_contain_the_order_id_metadata_or_invoice_id()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var (id, token, _) = await CreateConfidentialInvoiceAsync(api, "idem-secret-key-1");
        await api.PayAsync(id, "4", confirm: true); // underpaid: totals and a payment URI are shown too

        var html = await api.Anonymous.GetStringAsync($"/pay/{token}");
        var json = await api.Anonymous.GetStringAsync($"/api/public/invoices/{token}");
        var qr = await api.Anonymous.GetStringAsync($"/pay/{token}/qr.svg");

        foreach (var body in new[] { html, json, qr })
        {
            Assert.DoesNotContain(OrderId, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MetaKey, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MetaValue, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(id.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("idem-secret-key-1", body, StringComparison.Ordinal);
            Assert.DoesNotContain(TestStore.PayerAddress[2..], body, StringComparison.OrdinalIgnoreCase); // no sender addresses
        }

        var fields = JsonNode.Parse(json)!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);
        Assert.Equal(PublicFields.Order(StringComparer.Ordinal), fields);
    }

    [Fact]
    public async Task Page_shows_the_payment_details_from_the_link_returned_by_create()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var created = await api.CreateInvoiceJsonAsync("25.5");
        var pageUrl = new Uri(created["payment_page_url"]!.GetValue<string>());
        var payment = created["payment"]!;

        using var response = await api.Anonymous.GetAsync(pageUrl.PathAndQuery); // no API key: the page is public
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(Nethereum.Util.AddressUtil.Current.ConvertToChecksumAddress(payment["address"]!.GetValue<string>()), html, StringComparison.Ordinal);
        Assert.Contains(">25.5</span>", html, StringComparison.Ordinal);
        Assert.Contains("Anvil (local)", html, StringComparison.Ordinal);
        Assert.Contains("USDC", html, StringComparison.Ordinal);
        Assert.Contains($"datetime=\"{payment["expires_at"]!.GetValue<DateTimeOffset>().UtcDateTime:O}\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"initial-state\"", html, StringComparison.Ordinal);
        Assert.Contains("/pay/assets/pay.js?v=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_or_malformed_tokens_return_404_everywhere()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        await api.CreateInvoiceJsonAsync();
        var unknown = PaymentPageToken.Generate();

        using var json = await api.Anonymous.GetAsync($"/api/public/invoices/{unknown}");
        await ApiHarness.AssertProblemAsync(json, HttpStatusCode.NotFound, "invoice_not_found");

        using var page = await api.Anonymous.GetAsync($"/pay/{unknown}");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        Assert.Contains("Payment not found", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var qr = await api.Anonymous.GetAsync($"/pay/{unknown}/qr.svg");
        Assert.Equal(HttpStatusCode.NotFound, qr.StatusCode);

        foreach (var malformed in new[] { "abc", unknown + "x", unknown[..42] + "!", "' OR 1=1 --" })
        {
            using var r = await api.Anonymous.GetAsync($"/api/public/invoices/{Uri.EscapeDataString(malformed)}");
            await ApiHarness.AssertProblemAsync(r, HttpStatusCode.NotFound, "invoice_not_found");
        }

        // The Development sample is off unless configured.
        using var sample = await api.Anonymous.GetAsync("/pay/sample");
        Assert.Equal(HttpStatusCode.NotFound, sample.StatusCode);
    }

    [Fact]
    public async Task Status_json_follows_the_invoice_through_confirming_underpaid_and_paid()
    {
        await using var api = await ApiHarness.CreateAsync(postgres, new() { ["Chain:TokenAddress"] = TokenAddress });
        var (id, token, address) = await CreateConfidentialInvoiceAsync(api, null);
        var url = $"/api/public/invoices/{token}";

        var pending = await GetJsonAsync(api, url);
        Assert.Equal("pending", pending["status"]!.GetValue<string>());
        Assert.True(pending["accepting_payment"]!.GetValue<bool>());
        Assert.Equal("10", pending["remaining_amount"]!.GetValue<string>());
        Assert.Null(pending["confirmations"]);
        Assert.Equal(
            $"ethereum:{TokenAddress}@31337/transfer?address={address}&uint256=10000000",
            pending["payment_uri"]!.GetValue<string>());

        // Seen, not yet confirmed: confirming with a confirmation counter (block 101, head 101 → 1 of N).
        var first = await api.PayAsync(id, "4", confirm: false, n: 1);
        api.Factory.Services.GetRequiredService<ChainScanState>().ReportHead(101);
        var confirming = await GetJsonAsync(api, url);
        Assert.Equal("confirming", confirming["status"]!.GetValue<string>());
        Assert.Equal("4", confirming["pending_amount"]!.GetValue<string>());
        Assert.Equal(1, confirming["confirmations"]!["current"]!.GetValue<long>());
        Assert.Equal(2, confirming["confirmations"]!["required"]!.GetValue<int>());
        Assert.False(confirming["accepting_payment"]!.GetValue<bool>());
        Assert.Null(confirming["payment_uri"]);

        await api.Invoices.UpdateAsync(id, i => new InvoiceMutation<bool>(true, i.ConfirmTransfer(first.Id, api.Clock.GetUtcNow())));
        var underpaid = await GetJsonAsync(api, url);
        Assert.Equal("underpaid", underpaid["status"]!.GetValue<string>());
        Assert.Equal("4", underpaid["paid_amount"]!.GetValue<string>());
        Assert.Equal("6", underpaid["remaining_amount"]!.GetValue<string>());
        Assert.True(underpaid["accepting_payment"]!.GetValue<bool>());
        Assert.EndsWith("&uint256=6000000", underpaid["payment_uri"]!.GetValue<string>(), StringComparison.Ordinal);

        await api.PayAsync(id, "6", confirm: true, n: 2);
        var paid = await GetJsonAsync(api, url);
        Assert.Equal("paid", paid["status"]!.GetValue<string>());
        Assert.Equal("10", paid["paid_amount"]!.GetValue<string>());
        Assert.Equal("0", paid["remaining_amount"]!.GetValue<string>());
        Assert.False(paid["accepting_payment"]!.GetValue<bool>());
        Assert.Null(paid["payment_uri"]);
    }

    [Fact]
    public async Task Overpaid_cancelled_and_past_deadline_invoices_ask_for_no_payment()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);

        var over = await api.CreateInvoiceJsonAsync("10");
        await api.PayAsync(over["id"]!.GetValue<Guid>(), "12.5", confirm: true);
        var overpaid = await GetJsonAsync(api, PublicUrl(over));
        Assert.Equal("overpaid", overpaid["status"]!.GetValue<string>());
        Assert.Equal("2.5", overpaid["excess_amount"]!.GetValue<string>());
        Assert.False(overpaid["accepting_payment"]!.GetValue<bool>());

        var cancel = await api.CreateInvoiceJsonAsync("10");
        using (await api.Client.PostAsync($"/v1/invoices/{cancel["id"]}/cancel", null))
        {
        }

        var cancelled = await GetJsonAsync(api, PublicUrl(cancel));
        Assert.Equal("cancelled", cancelled["status"]!.GetValue<string>());
        Assert.Null(cancelled["payment_uri"]);

        // Deadline passed but the expiry worker has not run yet: still "pending", yet no longer accepting payment.
        var late = await api.CreateInvoiceJsonAsync("10");
        api.Clock.Advance(TimeSpan.FromHours(1));
        var pastDeadline = await GetJsonAsync(api, PublicUrl(late));
        Assert.Equal("pending", pastDeadline["status"]!.GetValue<string>());
        Assert.False(pastDeadline["accepting_payment"]!.GetValue<bool>());
        Assert.Null(pastDeadline["payment_uri"]);
    }

    [Fact]
    public async Task Responses_are_not_cached_leak_no_referrer_and_the_page_has_a_strict_csp()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var created = await api.CreateInvoiceJsonAsync();
        var token = Token(created);

        foreach (var path in new[] { $"/pay/{token}", $"/api/public/invoices/{token}", $"/pay/{token}/qr.svg" })
        {
            using var r = await api.Anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("no-store", r.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("noindex", r.Headers.GetValues("X-Robots-Tag").Single(), StringComparison.Ordinal);
        }

        using var page = await api.Anonymous.GetAsync($"/pay/{token}");
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);

        using var qr = await api.Anonymous.GetAsync($"/pay/{token}/qr.svg?mode=address");
        Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<svg", (await qr.Content.ReadAsStringAsync()).TrimStart(), StringComparison.Ordinal);

        foreach (var asset in new[] { "pay.js", "pay.css" })
        {
            using var r = await api.Anonymous.GetAsync($"/pay/assets/{asset}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        using var missingAsset = await api.Anonymous.GetAsync("/pay/assets/other.js");
        Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);
    }

    [Fact]
    public async Task Html_special_characters_in_configuration_are_encoded()
    {
        await using var api = await ApiHarness.CreateAsync(postgres, new() { ["PaymentPage:MerchantDisplayName"] = "<script>alert(1)</script> Shop" });
        var created = await api.CreateInvoiceJsonAsync();

        var html = await api.Anonymous.GetStringAsync($"/pay/{Token(created)}");

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; Shop", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_endpoints_are_rate_limited_per_client()
    {
        await using var api = await ApiHarness.CreateAsync(postgres, new() { ["PaymentPage:RateLimitPerMinute"] = "3" });
        var url = PublicUrl(await api.CreateInvoiceJsonAsync());

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var r = await api.Anonymous.GetAsync(url);
            codes.Add(r.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], codes);
        using var merchant = await api.Client.GetAsync("/v1/invoices"); // the merchant API is not affected
        Assert.Equal(HttpStatusCode.OK, merchant.StatusCode);
    }

    [Fact]
    public async Task Scripted_payer_reads_the_payment_details_from_the_public_endpoint()
    {
        await using var api = await ApiHarness.CreateAsync(postgres);
        var created = await api.CreateInvoiceJsonAsync("12.34");

        using var http = api.Factory.CreateClient();
        var reference = AcceptCrypto.Payer.InvoiceReference.Parse(Token(created), http.BaseAddress!);
        var details = await new AcceptCrypto.Payer.PaymentDetailsClient(http).GetAsync(reference, apiKey: null, CancellationToken.None);

        Assert.Equal(created["payment"]!["address"]!.GetValue<string>(), details.Address, ignoreCase: true);
        Assert.Equal(UsdcAmount.Parse("12.34"), details.Amount);
        Assert.Equal(31337, details.ChainId);
        Assert.Equal("pending", details.Status);
    }

    [Fact]
    public async Task Sample_page_works_without_an_invoice_when_enabled()
    {
        await using var api = await ApiHarness.CreateAsync(postgres, new() { ["PaymentPage:EnableSample"] = "true" });

        var json = await GetJsonAsync(api, "/api/public/invoices/sample?status=underpaid");
        Assert.Equal("underpaid", json["status"]!.GetValue<string>());
        using var page = await api.Anonymous.GetAsync("/pay/sample?status=confirming");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Fact]
    public void Eip681_uri_carries_token_chain_recipient_and_base_units()
    {
        Assert.Equal(
            "ethereum:0x5FbDB2315678afecb367f032d93F642f64180aa3@84532/transfer?address=0x9858EfFD232B4033E47d90003D41EC34EcaEda94&uint256=1000001",
            Eip681.TokenTransfer("0x5FbDB2315678afecb367f032d93F642f64180aa3", 84532, "0x9858EfFD232B4033E47d90003D41EC34EcaEda94", UsdcAmount.Parse("1.000001")));
        Assert.Null(Eip681.TokenTransfer(null, 1, "0x9858EfFD232B4033E47d90003D41EC34EcaEda94", UsdcAmount.Parse("1")));
    }

    private static async Task<(Guid Id, string Token, string Address)> CreateConfidentialInvoiceAsync(ApiHarness api, string? idempotencyKey)
    {
        using var response = await api.CreateInvoiceAsync(
            new { amount = "10", external_order_id = OrderId, metadata = new Dictionary<string, string> { [MetaKey] = MetaValue } },
            idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiHarness.ReadJsonAsync(response);
        var address = Nethereum.Util.AddressUtil.Current.ConvertToChecksumAddress(body["payment"]!["address"]!.GetValue<string>());
        return (body["id"]!.GetValue<Guid>(), Token(body), address);
    }

    private static string Token(JsonObject created) =>
        new Uri(created["payment_page_url"]!.GetValue<string>()).Segments[^1];

    private static string PublicUrl(JsonObject created) => $"/api/public/invoices/{Token(created)}";

    private static async Task<JsonObject> GetJsonAsync(ApiHarness api, string url)
    {
        using var response = await api.Anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiHarness.ReadJsonAsync(response);
    }
}
