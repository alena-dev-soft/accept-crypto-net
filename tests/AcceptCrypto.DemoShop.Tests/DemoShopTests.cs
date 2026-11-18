using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AcceptCrypto.DemoShop.Tests;

public sealed class DemoShopTests : IDisposable
{
    private const string Secret = "test_webhook_secret_000000000000000000000";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "demoshop-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAcceptCryptoClient _client = new();
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private (WebApplicationFactory<Program> Factory, HttpClient Http) Start(string? payerCommand = null)
    {
        WebApplicationFactory<Program>? factory = null;
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("AcceptCrypto:BaseUrl", "http://service.test");
            b.UseSetting("AcceptCrypto:ApiKey", "test_api_key");
            b.UseSetting("AcceptCrypto:WebhookSecret", Secret);
            b.UseSetting("Shop:DataPath", Path.Combine(_dataDir, "shop.json"));
            b.UseSetting("Shop:SelfUrl", "http://localhost");
            if (payerCommand is not null)
            {
                b.UseSetting("Shop:PayerCommand", payerCommand);
            }

            b.ConfigureServices(s =>
            {
                s.AddSingleton<IAcceptCryptoClient>(_client);
                s.AddHttpClient("attacks").ConfigurePrimaryHttpMessageHandler(() => factory!.Server.CreateHandler());
            });
        });
        _factories.Add(factory);
        return (factory, factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
    }

    [Fact]
    public async Task Checkout_creates_an_invoice_through_the_sdk_and_redirects_to_the_payment_page()
    {
        var (_, http) = Start();

        var response = await http.PostAsync("/checkout", Form("tshirt"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(FakeAcceptCryptoClient.PaymentPage, response.Headers.Location);
        var (request, key) = Assert.Single(_client.Created);
        Assert.Equal(UsdcAmount.Parse("25.00"), request.Amount);
        Assert.Equal("order-" + request.ExternalOrderId, key);
        var order = Assert.Single(await Orders(http));
        Assert.Equal(request.ExternalOrderId, order.GetProperty("id").GetString());
        Assert.Equal("pending", order.GetProperty("status").GetString());
        Assert.Equal(FakeAcceptCryptoClient.PaymentPage.ToString(), order.GetProperty("paymentPageUrl").GetString());
    }

    [Fact]
    public async Task Checkout_with_the_short_lifetime_asks_for_a_two_minute_invoice()
    {
        var (_, http) = Start();

        await http.PostAsync("/checkout", Form("stickers", lifetime: "2"));

        Assert.Equal(TimeSpan.FromMinutes(2), Assert.Single(_client.Created).Request.ExpiresIn);
    }

    [Fact]
    public async Task Checkout_when_the_service_is_down_marks_the_order_failed()
    {
        var (_, http) = Start();
        _client.Fail = true;

        var response = await http.PostAsync("/checkout", Form("tshirt"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("invoice_failed", Assert.Single(await Orders(http)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_signed_webhook_updates_the_order_and_a_duplicate_is_not_applied_twice()
    {
        var (factory, http) = Start();
        var orderId = await CheckoutAsync(http);
        var paid = Event(orderId, "paid", "confirming", DateTimeOffset.UtcNow);

        var first = await http.SendAsync(Signed(paid));
        var duplicate = await http.SendAsync(Signed(paid));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal("paid", (await Orders(http))[0].GetProperty("status").GetString());
        var log = factory.Services.GetRequiredService<Shop.ShopStore>().WebhookLog();
        Assert.Equal([Shop.WebhookOutcome.Duplicate, Shop.WebhookOutcome.Processed], log.Select(e => e.Outcome));
    }

    [Fact]
    public async Task An_older_event_does_not_move_the_order_back()
    {
        var (factory, http) = Start();
        var orderId = await CheckoutAsync(http);
        var now = DateTimeOffset.UtcNow;

        await http.SendAsync(Signed(Event(orderId, "paid", "confirming", now)));
        await http.SendAsync(Signed(Event(orderId, "confirming", "pending", now.AddSeconds(-10))));

        Assert.Equal("paid", (await Orders(http))[0].GetProperty("status").GetString());
        Assert.Equal(Shop.WebhookOutcome.Stale, factory.Services.GetRequiredService<Shop.ShopStore>().WebhookLog()[0].Outcome);
    }

    [Fact]
    public async Task Forged_tampered_and_stale_webhooks_are_rejected_and_shown_in_the_log()
    {
        var (factory, http) = Start();
        var orderId = await CheckoutAsync(http);
        await http.SendAsync(Signed(Event(orderId, "confirming", "pending", DateTimeOffset.UtcNow)));

        var results = new Dictionary<string, int>();
        foreach (var kind in new[] { "forged", "tampered", "replay", "stale" })
        {
            var response = await http.PostAsync(new Uri($"/demo/attacks/{kind}", UriKind.Relative), null);
            response.EnsureSuccessStatusCode();
            results[kind] = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("statusCode").GetInt32();
        }

        Assert.Equal(400, results["forged"]);
        Assert.Equal(400, results["tampered"]);
        Assert.Equal(200, results["replay"]); // within the tolerance: verified, but detected as a duplicate
        Assert.Equal(400, results["stale"]);
        Assert.Equal("confirming", (await Orders(http))[0].GetProperty("status").GetString()); // nothing marked it paid
        var log = factory.Services.GetRequiredService<Shop.ShopStore>().WebhookLog();
        Assert.Equal(
            [("stale", "rejected"), ("replay", "duplicate"), ("tampered", "rejected"), ("forged", "rejected")],
            log.Take(4).Select(e => (e.Attack!, e.Outcome)));
        Assert.Contains("TimestampOutsideTolerance", log[0].Detail, StringComparison.Ordinal);
        Assert.Contains("SignatureMismatch", log[3].Detail, StringComparison.Ordinal);

        var page = await http.GetStringAsync(new Uri("/webhooks", UriKind.Relative));
        Assert.Contains("demo: forged", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orders_and_processed_event_ids_survive_a_restart_of_the_shop()
    {
        var (first, http) = Start();
        var orderId = await CheckoutAsync(http);
        var paid = Event(orderId, "paid", "confirming", DateTimeOffset.UtcNow);
        await http.SendAsync(Signed(paid));
        await first.DisposeAsync();

        var (second, http2) = Start();
        var redelivered = await http2.SendAsync(Signed(paid));

        Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
        Assert.Equal("paid", Assert.Single(await Orders(http2)).GetProperty("status").GetString());
        Assert.Equal(Shop.WebhookOutcome.Duplicate, second.Services.GetRequiredService<Shop.ShopStore>().WebhookLog()[0].Outcome);
    }

    [Fact]
    public async Task Pages_render()
    {
        var (_, http) = Start();
        var orderId = await CheckoutAsync(http);

        Assert.Contains("Buy with USDC", await http.GetStringAsync(new Uri("/", UriKind.Relative)), StringComparison.Ordinal);
        Assert.Contains(orderId, await http.GetStringAsync(new Uri("/orders", UriKind.Relative)), StringComparison.Ordinal);
        var orderPage = await http.GetStringAsync(new Uri("/orders/" + orderId, UriKind.Relative));
        Assert.Contains("Open the payment page", orderPage, StringComparison.Ordinal);
        Assert.Contains("Payment service view", orderPage, StringComparison.Ordinal);
        Assert.Contains("scripted payer", orderPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testnet_profile_hides_the_scripted_payer_hint()
    {
        var (_, http) = Start(payerCommand: "");
        var orderId = await CheckoutAsync(http);

        var orderPage = await http.GetStringAsync(new Uri("/orders/" + orderId, UriKind.Relative));
        Assert.Contains("Open the payment page", orderPage, StringComparison.Ordinal);
        Assert.DoesNotContain("scripted payer", orderPage, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static FormUrlEncodedContent Form(string product, string lifetime = "") =>
        new(new Dictionary<string, string> { ["product"] = product, ["lifetime"] = lifetime });

    private async Task<string> CheckoutAsync(HttpClient http)
    {
        await http.PostAsync("/checkout", Form("tshirt"));
        return _client.Created[^1].Request.ExternalOrderId;
    }

    private static async Task<JsonElement[]> Orders(HttpClient http) =>
        (await http.GetFromJsonAsync<JsonElement[]>(new Uri("/api/orders", UriKind.Relative)))!;

    private static string Event(string orderId, string status, string previous, DateTimeOffset createdAt) =>
        JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(),
            type = "invoice." + status,
            created_at = createdAt,
            api_version = "2026-11-17",
            data = new
            {
                invoice = new
                {
                    id = FakeAcceptCryptoClient.InvoiceId,
                    external_order_id = orderId,
                    status,
                    amount = "25",
                    confirmed_total = status == "paid" ? "25" : "0",
                    remaining_amount = status == "paid" ? "0" : "25",
                    deposit_address = "0x8ba1f109551bd432803012645ac136ddd64dba72",
                    created_at = createdAt.AddMinutes(-1),
                    expires_at = createdAt.AddMinutes(29),
                    metadata = new Dictionary<string, string>(),
                },
                previous_status = previous,
            },
        });

    private static HttpRequestMessage Signed(string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/acceptcrypto") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(AcceptCryptoWebhookVerifier.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(AcceptCryptoWebhookVerifier.SignatureHeader, AcceptCryptoWebhookVerifier.ComputeSignatureHeader([Secret], timestamp, bytes));
        return request;
    }

    private sealed class FakeAcceptCryptoClient : IAcceptCryptoClient
    {
        public static readonly Guid InvoiceId = Guid.Parse("0193a1b0-0000-7000-8000-000000000001");
        public static readonly Uri PaymentPage = new("http://localhost:5052/pay/test-token");

        public List<(CreateInvoiceRequest Request, string? Key)> Created { get; } = [];

        public bool Fail { get; set; }

        public Task<CreatedInvoice> CreateInvoiceAsync(CreateInvoiceRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new AcceptCryptoConnectionException("Cannot reach the AcceptCrypto service.");
            }

            Created.Add((request, idempotencyKey));
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CreatedInvoice
            {
                Id = InvoiceId,
                Status = InvoiceStatus.Pending,
                Amount = request.Amount,
                Currency = "USDC",
                ExternalOrderId = request.ExternalOrderId,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(30),
                UpdatedAt = now,
                PaymentPageUrl = PaymentPage,
                Payment = new PaymentDetails
                {
                    Network = "Anvil (local)",
                    ChainId = 31337,
                    Token = "USDC",
                    Decimals = 6,
                    Address = "0x8ba1f109551bd432803012645ac136ddd64dba72",
                    Amount = request.Amount,
                    RemainingAmount = request.Amount,
                    ExpiresAt = now.AddMinutes(30),
                },
            });
        }

        public Task<InvoiceDetails> GetInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
            throw new AcceptCryptoConnectionException("Not available in tests.");

        public Task<Page<Invoice>> ListInvoicesAsync(ListInvoicesQuery? query = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<Invoice> ListAllInvoicesAsync(ListInvoicesQuery? query = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Invoice> CancelInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Page<UnmatchedPayment>> ListUnmatchedPaymentsAsync(ListUnmatchedPaymentsQuery? query = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<UnmatchedPayment> ListAllUnmatchedPaymentsAsync(ListUnmatchedPaymentsQuery? query = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WebhookEventInfo> ResendEventAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
