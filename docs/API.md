# Merchant API — AcceptCrypto.NET

The service's REST API for the merchant's backend (the SDK wraps it). Machine-readable description:
`GET /openapi/v1.json` (OpenAPI 3.1), interactive reference: `GET /scalar`.

- Base path: `/v1`. JSON is `snake_case`; enums are `snake_case` strings (`paid_late`).
- **Amounts** are decimal strings in USDC with at most 6 decimals (`"25.5"`), never JSON numbers (NFR-1).
  Responses use the canonical form (trailing zeros trimmed).
- **Timestamps** are ISO 8601 with offset, UTC.
- **Authentication** (FR-17): header `X-Api-Key: <Merchant:ApiKey>` on every `/v1` call. Missing or wrong key → `401`
  with `WWW-Authenticate: ApiKey header="X-Api-Key"`. Public: `/health`, `/health/ready`, `/openapi/v1.json`, `/scalar`.

## Endpoints

| Method & path | What | Success | Errors |
| --- | --- | --- | --- |
| `POST /v1/invoices` | Create an invoice (FR-1/2/3/4) | `201` + `Location`; `200` + `Idempotent-Replayed: true` on a replay | 400, 401, 422 |
| `GET /v1/invoices/{id}` | Invoice + transfers + webhook events with delivery attempts (FR-19) | `200` | 401, 404 |
| `GET /v1/invoices` | List, newest first, filters + cursor (FR-18) | `200` | 400, 401 |
| `POST /v1/invoices/{id}/cancel` | Cancel a `pending` invoice (D6) | `200` (also when already cancelled) | 401, 404, 409 |
| `GET /v1/unmatched-payments` | Transfers that matched no open invoice (FR-11a) | `200` | 400, 401 |
| `POST /v1/events/{eventId}/resend` | Queue a webhook event again, same event ID and payload (FR-15) | `202` | 401, 404, 409 |

### Create

```http
POST /v1/invoices
X-Api-Key: …
Idempotency-Key: 6c0f5a52-4d0e-4f0e-9a51-3b1f6f0f2a10
Content-Type: application/json

{"amount": "25.50", "external_order_id": "order-42", "metadata": {"customer": "c-1"}, "expires_in_seconds": 1800}
```

- `amount` (required), `external_order_id` (required, ≤ 200 chars), `metadata` (optional string map, ≤ 20 entries,
  key ≤ 40, value ≤ 500 chars; never shown to the payer), `expires_in_seconds` (optional, within
  `Invoices:MinLifetime`…`MaxLifetime`, default `Invoices:DefaultLifetime`).
- `Idempotency-Key` (optional, recommended; 1–255 printable ASCII): the same key with the same request returns the
  original invoice (`200`, `Idempotent-Replayed: true`) — also for parallel requests. The same key with a different
  request → `422 idempotency_key_reused` (with `invoice_id`). The comparison uses a SHA-256 fingerprint of the
  canonical request (amount value, order ID, metadata sorted by key, lifetime), so `"10"` and `"10.000"` are the same
  request. A request that fails validation does not consume its key. Keys do not expire (single merchant, D12).

Response (also the shape of list items):

```json
{
  "id": "01a1e8ab-abcf-7719-a329-5a51123c22f6",
  "status": "pending",
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
  }
}
```

`payment` is exactly what the payer needs (FR-4); the payment page (step 8) shows only that.
`payment_page_url` = `Api:PublicBaseUrl` (or the request's scheme/host) + `/pay/{unguessable token}` (FR-29).

### Details (`GET /v1/invoices/{id}`)

The invoice plus:
- `transfers[]`: `tx_hash`, `log_index`, `from`, `amount`, `block_number`, `block_hash`, `block_timestamp`, `on_time`,
  `state` (`seen` / `confirmed` / `reverted`), `confirmations` (from the scanner's last head; null if unknown or
  reverted), `seen_at`, `confirmed_at`, `reverted_at`.
- `events[]` in causal order: `id` (= webhook event ID), `type` (`invoice.paid`, `payment.reverted`, …),
  `delivery_status` (`pending` / `delivered` / `failed`), `attempts`, `next_attempt_at`, `delivered_at`, `last_error`,
  `delivery_attempts[]` (`attempted_at`, `http_status`, `error`, `duration_ms`).

### Lists (`GET /v1/invoices`, `GET /v1/unmatched-payments`)

Query: `limit` (1…`Api:MaxPageSize`, default `Api:DefaultPageSize`), `cursor` (the previous page's `next_cursor`).
Invoices also: `status` (`pending`, `confirming`, `underpaid`, `paid`, `overpaid`, `expired`, `paid_late`,
`cancelled`), `created_from` (inclusive), `created_to` (exclusive), ISO 8601.
Response: `{"data": [...], "next_cursor": "…" | null}`. Keyset pagination on `(created_at, id)` (unmatched: `id`), so
pages stay stable while new rows arrive. Cursors are opaque.

Unmatched payment: `id`, `chain_id`, `tx_hash`, `log_index`, `block_number`, `block_hash`, `block_timestamp`, `from`,
`to`, `amount`, `reason` (`unknown_address` / `invoice_closed`), `invoice_id` (the closed invoice, if any), `recorded_at`.

### Resend

Resets the event to `pending` with a fresh retry budget and `next_attempt_at = now`; the event ID and the payload
bytes are unchanged, so the merchant deduplicates by event ID (FR-16). Delivery history is kept.

## Errors (FR-20)

Every error is RFC 9457 Problem Details (`application/problem+json`) with a stable `code` extension and `trace_id`:

```json
{
  "type": "https://github.com/alena-dev-soft/accept-crypto-net/blob/main/docs/API.md#validation-failed",
  "title": "Validation failed",
  "status": 400,
  "detail": "One or more request fields are invalid.",
  "instance": "/v1/invoices",
  "errors": { "amount": ["Must be a decimal string with at most 6 decimals, e.g. \"25.50\"."] },
  "code": "validation_failed",
  "trace_id": "00-…-00"
}
```

### validation-failed
`400`. `errors` maps a field (JSON name, or `Idempotency-Key`) to messages.

### malformed-request
`400`. The body is not valid JSON for the operation (e.g. `amount` sent as a number); `detail` gives the JSON path.

### api-key-missing
`401`. No `X-Api-Key` header.

### api-key-invalid
`401`. Wrong key.

### not-found
`404`. Unknown route (or an ID that is not a UUID).

### invoice-not-found
`404`.

### event-not-found
`404`.

### invoice-not-cancellable
`409`. The invoice is not `pending`; `current_status` tells which status it is in.

### idempotency-key-reused
`422`. Same `Idempotency-Key`, different request; `invoice_id` is the invoice created with that key.

### conflict
`409`. The event was being updated concurrently several times in a row; retry.

### internal-error
`500`. Logged with the `trace_id`; no internals in the response.

## Configuration (FR-0)

All from appsettings / environment variables (`Merchant__ApiKey`) / user-secrets; validated at startup — the service
does not start with missing or invalid values, and validation messages never contain secret values (NFR-5).

| Setting | Meaning |
| --- | --- |
| `Merchant:ApiKey` | Secret, ≥ 32 chars, no whitespace (`openssl rand -hex 32`) |
| `Merchant:WebhookUrl` | https URL; http, private IPs and localhost only for hosts on `Webhooks:AllowedHosts` |
| `Merchant:WebhookSecret` | Secret, ≥ 32 chars; signs webhooks (step 7) |
| `Merchant:PreviousWebhookSecret` | Optional, only during rotation; must differ from the current one |
| `Merchant:Xpub` | Extended public key at `m/44'/60'/0'/0` (xpub/tpub); xprv is refused |
| `Invoices:MinAmount` / `MaxAmount` | Decimal strings (default `0.01` / `1000000`) |
| `Invoices:DefaultLifetime` / `MinLifetime` / `MaxLifetime` | Default 30 min / 1 min / 7 days |
| `Webhooks:AllowedHosts` | SSRF allowlist (e.g. `demo-shop`), exact host match |
| `Webhooks:InitialRetryDelay` / `MaxRetryDelay` / `MaxAttempts` / `RetryWindow` / `RequestTimeout` | Retry schedule (default 30 s doubling to 1 h, 30 attempts, 24 h window, 10 s timeout) |
| `Api:PublicBaseUrl` | Base of payment page links; default: the request's scheme/host |
| `Api:DefaultPageSize` / `MaxPageSize` | 50 / 200 |
| `Chain:ChainId`, `Chain:NetworkName`, `Chain:TokenSymbol`, `Chain:TokenAddress`, `Chain:Confirmations` | Shown in payment details; scanner settings see DECISIONS D14 |

`appsettings.Development.json` contains throwaway development values (dummy key/secret, the public test mnemonic's
xpub); the Preview (`scripts/dev.sh`) runs with them.

## Health (NFR-7)

- `GET /health` — liveness: `Healthy` / `Degraded` (scanner check when the scanner is enabled). No dependencies, so it
  stays green without a database.
- `GET /health/ready` — readiness: also `database` (PostgreSQL reachable) and `rpc` (node reachable and on
  `Chain:ChainId`, only when `Chain:Enabled`). JSON with per-check status; `503` when unhealthy. No exception texts.
