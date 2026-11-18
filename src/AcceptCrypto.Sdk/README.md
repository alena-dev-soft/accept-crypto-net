# AcceptCrypto.NET SDK for ASP.NET Core

Accept USDC in your ASP.NET Core app with the [AcceptCrypto.NET](https://github.com/alena-dev-soft/accept-crypto-net)
payment service: a typed API client and a one-line, signature-verified webhook endpoint. Non-custodial — the service
never holds keys; payments go to deposit addresses derived from your xpub.

- **Client** for every merchant API operation: create (idempotent), get, list (filters + cursor), cancel invoices;
  list unmatched payments; resend webhook events.
- **Errors** as typed exceptions built from the service's RFC 9457 Problem Details.
- **Webhooks**: HMAC-SHA256 verification in constant time, several secrets for rotation, replay protection
  (timestamp tolerance, default 5 min), typed events, duplicate detection by event ID.
- **Money** as `UsdcAmount` (integer base units, 6 decimals) — never `double`, never a lossy JSON number.

Targets `net10.0`. Package ID: `AcceptCrypto.NET`.

## Install

```bash
dotnet add package AcceptCrypto.NET --prerelease
```

## Configure

`appsettings.json` (keep `ApiKey` and `WebhookSecret` in user-secrets / environment variables, e.g.
`AcceptCrypto__ApiKey`, not in the file):

```json
{
  "AcceptCrypto": {
    "BaseUrl": "https://pay.example.com",
    "ApiKey": "<Merchant:ApiKey of the service>",
    "WebhookSecret": "<Merchant:WebhookSecret of the service>"
  }
}
```

## Minimal integration

```csharp
using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAcceptCrypto();                 // binds the "AcceptCrypto" section, registers IAcceptCryptoClient
var app = builder.Build();

// 1. Checkout: create an invoice and send the customer to the payment page.
app.MapPost("/checkout/{orderId}", async (string orderId, IAcceptCryptoClient acceptCrypto) =>
{
    var invoice = await acceptCrypto.CreateInvoiceAsync(
        new CreateInvoiceRequest { Amount = UsdcAmount.Parse("25.00"), ExternalOrderId = orderId },
        idempotencyKey: $"order-{orderId}");      // a retry never creates a second invoice
    return Results.Redirect(invoice.PaymentPageUrl.ToString());
});

// 2. Webhooks: verified, parsed, deduplicated — in one line.
app.MapAcceptCryptoWebhook("/webhooks/acceptcrypto", async (AcceptCryptoEvent e) =>
{
    if (e is InvoiceStatusChangedEvent { Status: InvoiceStatus.Paid or InvoiceStatus.PaidLate } paid)
    {
        await MarkOrderPaidAsync(paid.Invoice.ExternalOrderId);   // your code
    }
});

app.Run();
```

Point the service's `Merchant:WebhookUrl` at `https://your-app/webhooks/acceptcrypto`.

## The client

```csharp
InvoiceDetails details = await client.GetInvoiceAsync(invoiceId);      // + transfers and webhook history
Page<Invoice> page = await client.ListInvoicesAsync(new ListInvoicesQuery { Status = InvoiceStatus.Paid, Limit = 50 });
await foreach (var invoice in client.ListAllInvoicesAsync(new() { CreatedFrom = DateTimeOffset.UtcNow.AddDays(-1) })) { … }
Invoice cancelled = await client.CancelInvoiceAsync(invoiceId);         // only pending invoices
await foreach (var transfer in client.ListAllUnmatchedPaymentsAsync()) { … }
WebhookEventInfo resent = await client.ResendEventAsync(eventId);       // same event ID, same payload
```

- `CreateInvoiceAsync` returns `CreatedInvoice`; `IdempotentReplayed` is true when the service returned the invoice it
  had created earlier for the same key. Without a key the SDK generates one per call (safe HTTP-level retries); pass
  your own stable key (e.g. the order ID) to be safe across restarts too.
- `AddAcceptCrypto` returns the typed client's `IHttpClientBuilder`, so you can add e.g.
  `.AddStandardResilienceHandler()` (Microsoft.Extensions.Http.Resilience). Creating invoices is safe to retry
  because of the idempotency key.
- Overloads: `AddAcceptCrypto(o => …)` (section + overrides) and `AddAcceptCrypto(configuration.GetSection("…"))`.
  Without DI: `new AcceptCryptoClient(new HttpClient(), new AcceptCryptoOptions { … })`.

### Errors

| Exception | When |
| --- | --- |
| `AcceptCryptoValidationException` | 400 — `ValidationErrors` maps fields to messages |
| `AcceptCryptoAuthenticationException` | 401 — API key missing or wrong |
| `AcceptCryptoNotFoundException` | 404 — no such invoice / event |
| `AcceptCryptoConflictException` | 409 — e.g. cancelling a paid invoice (`CurrentStatus`) |
| `AcceptCryptoIdempotencyKeyReusedException` | 422 — same key, different request (`InvoiceId`) |
| `AcceptCryptoApiException` | any other error status; base of the above (`StatusCode`, `ErrorCode`, `TraceId`, `Extensions`) |
| `AcceptCryptoConnectionException` | service unreachable or timeout |

All derive from `AcceptCryptoException`. Your own cancellation surfaces as `OperationCanceledException`.

## Webhooks

`MapAcceptCryptoWebhook(pattern, handler)` maps `POST pattern` and, for every request:

1. reads the raw body (limit `WebhookMaxBodyBytes`, 1 MiB) and the `AcceptCrypto-Timestamp` / `AcceptCrypto-Signature`
   headers;
2. rejects with **400** when a header is missing, the timestamp is more than `WebhookTolerance` (5 min) away from now
   (replay), or no `v1=` signature matches any configured secret (forged / altered) — compared with
   `CryptographicOperations.FixedTimeEquals`;
3. parses a typed event: `InvoiceStatusChangedEvent` (`invoice.*`, with `Status` and `PreviousStatus`),
   `PaymentRevertedEvent` (`payment.reverted`, a reorg removed a payment), `UnknownAcceptCryptoEvent` (newer types);
4. skips duplicates via `IWebhookEventDeduplicator` (answers **200** without calling you; **409** while the same event is
   still being processed);
5. calls your handler; **200** on success, **500** if it throws — the service then retries with backoff.

Handler variants: `async (AcceptCryptoEvent e) => …`, `async (AcceptCryptoEvent e, HttpContext ctx) => …`
(services via `ctx.RequestServices`, cancellation via `ctx.RequestAborted`) or a class:
`app.MapAcceptCryptoWebhook<OrderPaymentsHandler>("/webhooks/acceptcrypto")` with
`OrderPaymentsHandler : IAcceptCryptoWebhookHandler`.

**Duplicates (FR-16).** Delivery is at least once. The default `InMemoryWebhookEventDeduplicator` remembers handled
event IDs for 7 days in one process. With several instances, or to survive restarts, register your own
implementation — typically an insert into a table with a unique index on the event ID in the same transaction as your
order update:

```csharp
builder.Services.AddSingleton<IWebhookEventDeduplicator, DbWebhookEventDeduplicator>();
```

**Secret rotation.** Accept the new and the old secret while the service switches:

```json
{ "AcceptCrypto": { "WebhookSecret": "<new>", "AdditionalWebhookSecrets": [ "<old>" ] } }
```

Options are read per request (`IOptionsMonitor`), so a configuration reload takes effect without a restart. Full
rotation procedure: [docs/webhooks.md §5](https://github.com/alena-dev-soft/accept-crypto-net/blob/main/docs/webhooks.md).

**Without the endpoint helper** (e.g. a controller or Azure Function):

```csharp
var verifier = new AcceptCryptoWebhookVerifier([secret]);           // or inject AcceptCryptoWebhookVerifier
AcceptCryptoEvent e = verifier.VerifyAndParse(timestampHeader, signatureHeader, rawBodyBytes); // throws AcceptCryptoWebhookException
```

`AcceptCryptoWebhookVerifier.ComputeSignatureHeader(secrets, timestamp, body)` signs test payloads.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `BaseUrl` | — | Service base URL (required for the client) |
| `ApiKey` | — | Merchant API key, sent as `X-Api-Key` (required for the client) |
| `Timeout` | 30 s | Per API call |
| `GenerateIdempotencyKeys` | `true` | Generate a key when `CreateInvoiceAsync` gets none |
| `WebhookSecret` | — | Current webhook secret (required for the endpoint) |
| `AdditionalWebhookSecrets` | `[]` | Further accepted secrets (rotation) |
| `WebhookTolerance` | 5 min | Allowed timestamp difference (max 1 h) |
| `WebhookMaxBodyBytes` | 1 MiB | Larger bodies get 413 |

Options are validated at startup; messages never contain secret values. The API key is never logged by the SDK
(and `IHttpClientFactory` logging redacts header values).

## Amounts

`UsdcAmount` stores integer base units (`BigInteger`, 1 USDC = 1 000 000) and is a JSON **string** on the wire.
Create it with `UsdcAmount.Parse("25.50")`, `new UsdcAmount(25.50m)` (more than 6 decimals throws) or
`UsdcAmount.FromBaseUnits(25_500_000)`; `ToString()` gives the canonical form (`"25.5"`), `ToDecimal()` a `decimal`.

## License

MIT
