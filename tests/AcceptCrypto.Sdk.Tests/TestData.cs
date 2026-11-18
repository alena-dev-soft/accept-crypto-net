using System.Net;
using System.Text;

namespace AcceptCrypto.Sdk.Tests;

/// <summary>Payloads in the exact wire format of the service (docs/API.md, docs/webhooks.md).</summary>
internal static class TestData
{
    public const string ApiKey = "test_api_key_0123456789abcdef0123456789abcdef";
    public const string Secret = "whsec_test_0123456789abcdef0123456789abcdef";
    public const string OldSecret = "previous_secret_zzzzzzzzzzzzzzzzzzzzzzzzzzzz";

    public static readonly Guid InvoiceId = Guid.Parse("01a1e8ab-abcf-7719-a329-5a51123c22f6");
    public static readonly Guid EventId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    public static string InvoiceJson(string status = "pending", string extra = "") => $$"""
        {
          "id": "{{InvoiceId}}",
          "status": "{{status}}",
          "amount": "25.5",
          "currency": "USDC",
          "external_order_id": "order-42",
          "metadata": { "customer": "c-1" },
          "confirmed_total": "0", "unconfirmed_total": "0", "remaining_amount": "25.5", "excess_amount": "0",
          "created_at": "2026-11-17T08:42:07.18+00:00",
          "expires_at": "2026-11-17T09:12:07.18+00:00",
          "expired_at": null,
          "updated_at": "2026-11-17T08:42:07.18+00:00",
          "payment_page_url": "https://pay.example.com/pay/86CqUPAR7QCD6YAgyFmQV7vmI3LRUQMQQ9PMSND5xK4",
          "payment": {
            "network": "Anvil (local)", "chain_id": 31337, "token": "USDC",
            "token_address": "0x5FbDB2315678afecb367f032d93F642f64180aa3", "decimals": 6,
            "address": "0x9858effd232b4033e47d90003d41ec34ecaeda94",
            "amount": "25.5", "remaining_amount": "25.5", "expires_at": "2026-11-17T09:12:07.18+00:00"
          }{{extra}}
        }
        """;

    public static string DetailsJson() => InvoiceJson("underpaid", $$"""
        ,
          "transfers": [{
            "tx_hash": "0xabc", "log_index": 3, "from": "0x70997970C51812dc3A010C7d01b50e0d17dc79C8", "amount": "10",
            "block_number": 120, "block_hash": "0xdef", "block_timestamp": "2026-11-17T08:50:00+00:00", "on_time": true,
            "state": "confirmed", "confirmations": 5, "seen_at": "2026-11-17T08:50:01+00:00",
            "confirmed_at": "2026-11-17T08:50:09+00:00", "reverted_at": null
          }],
          "events": [{
            "id": "{{EventId}}", "type": "invoice.underpaid", "invoice_id": "{{InvoiceId}}", "delivery_status": "failed",
            "attempts": 2, "created_at": "2026-11-17T08:50:09+00:00", "next_attempt_at": null, "delivered_at": null,
            "last_error": "HTTP 503",
            "delivery_attempts": [
              { "attempted_at": "2026-11-17T08:50:10+00:00", "http_status": 503, "error": "HTTP 503", "duration_ms": 12 },
              { "attempted_at": "2026-11-17T08:50:40+00:00", "http_status": null, "error": "Connection refused", "duration_ms": 3 }
            ]
          }]
        """);

    public static string EventInfoJson(string status = "pending") => $$"""
        { "id": "{{EventId}}", "type": "invoice.paid", "invoice_id": "{{InvoiceId}}", "delivery_status": "{{status}}",
          "attempts": 0, "created_at": "2026-11-17T08:50:09+00:00", "next_attempt_at": "2026-11-17T09:00:00+00:00",
          "delivered_at": null, "last_error": null, "delivery_attempts": [] }
        """;

    public const string PaidEventJson = """
        {"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid","created_at":"2026-11-20T12:03:10.512+00:00","api_version":"2026-11-17","data":{"invoice":{"id":"0193a1b0-0000-7000-8000-000000000001","external_order_id":"order-1001","status":"paid","amount":"10","confirmed_total":"10","remaining_amount":"0","deposit_address":"0x8ba1f109551bd432803012645ac136ddd64dba72","created_at":"2026-11-20T12:00:00+00:00","expires_at":"2026-11-20T12:30:00+00:00","metadata":{"customer":"42"}},"previous_status":"confirming"}}
        """;

    public const string RevertedEventJson = """
        {"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a60","type":"payment.reverted","created_at":"2026-11-20T12:03:10+00:00","api_version":"2026-11-17","data":{"invoice":{"id":"0193a1b0-0000-7000-8000-000000000001","external_order_id":"order-1001","status":"confirming","amount":"10","confirmed_total":"0","remaining_amount":"10","deposit_address":"0x8ba1f109551bd432803012645ac136ddd64dba72","created_at":"2026-11-20T12:00:00+00:00","expires_at":"2026-11-20T12:30:00+00:00","metadata":{}},"transfer":{"tx_hash":"0x01","log_index":0,"amount":"4.000001","block_number":77,"block_hash":"0x02"}}}
        """;

    public static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Problem(HttpStatusCode status, string code, string extra = "") => Json(status, $$"""
        {"type":"https://github.com/alena-dev-soft/accept-crypto-net/blob/main/docs/API.md#{{code.Replace('_', '-')}}",
         "title":"T","status":{{(int)status}},"detail":"D","instance":"/v1/x","code":"{{code}}","trace_id":"00-abc-00"{{extra}}}
        """, "application/problem+json");
}

/// <summary>Records requests and answers them with a scripted function.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request);
    }
}
