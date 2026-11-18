# Webhooks — wire format, signatures, delivery and secret rotation

Spec: FR-0, FR-12–FR-16, NFR-2, NFR-4, NFR-6, NFR-7. Code: `src/AcceptCrypto.Infrastructure/Webhooks`
(`WebhookDispatcher`, `WebhookSignature`, `WebhookRetryPolicy`, `WebhookMetrics`), payload:
`src/AcceptCrypto.Infrastructure/Persistence/OutboxEventFactory.cs`. Decisions: [DECISIONS.md](DECISIONS.md) D16.

This document is the contract between the service and the merchant's receiver (the SDK in step 9 implements the
receiving side of it).

## 1. The request

Every invoice status change and every reorg rollback creates one **event** (FR-12). The event is written to the
transactional outbox in the same database transaction as the invoice change (NFR-2) and delivered later by the
dispatcher as:

```http
POST {Merchant:WebhookUrl}
Content-Type: application/json; charset=utf-8
User-Agent: AcceptCrypto.NET-Webhooks/1.0
AcceptCrypto-Event-Id: 0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b
AcceptCrypto-Event-Type: invoice.paid
AcceptCrypto-Timestamp: 1763380800
AcceptCrypto-Signature: v1=029fe4509021b3db58be3326a42bc973e37369597e5c31fd92f8a64c3587f209

{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid",...}
```

| Header | Value |
| --- | --- |
| `AcceptCrypto-Event-Id` | The event ID (UUID). The same on every retry and manual resend — use it to deduplicate (FR-16). Equals `id` in the body. |
| `AcceptCrypto-Event-Type` | The event type (informational; equals `type` in the body). |
| `AcceptCrypto-Timestamp` | Unix time in seconds **of this delivery attempt** (a retry is signed again with a new timestamp). |
| `AcceptCrypto-Signature` | One or more `v1=<64 lowercase hex chars>` entries separated by `,`. Normally one; two during secret rotation (current secret first). Unknown schemes (anything other than `v1`) must be ignored. |

The receiver answers **any 2xx** to acknowledge. Everything else — another status, a timeout (default 10 s), a
connection error — is a failed attempt and is retried (section 4). Redirects are not followed.

## 2. Signature (FR-13)

```
signed_payload = "{AcceptCrypto-Timestamp}.{raw request body}"            (UTF-8 bytes)
signature      = lowercase_hex( HMAC-SHA256( key = UTF-8(secret), message = signed_payload ) )
header         = "v1=" + signature
```

The body is signed **byte for byte**: verify against the raw request body before parsing or re-serialising it.

**Verifying (what the SDK does, FR-26):**

1. Read `AcceptCrypto-Timestamp`, `AcceptCrypto-Signature` and the raw body; reject if any is missing.
2. Reject if `|now − timestamp| > tolerance` (default 5 minutes) — this stops replays of captured requests.
3. For every configured secret (the current one and, during rotation, the previous one) compute the expected
   signature and compare it with every `v1=` entry of the header **in constant time**
   (`CryptographicOperations.FixedTimeEquals`). Accept if any pair matches.
4. Deduplicate by `AcceptCrypto-Event-Id` (the same event can arrive more than once — section 4).

**Reference vectors** (also in `tests/AcceptCrypto.Infrastructure.Tests/Webhooks/WebhookSignatureTests.cs`; computed
independently with Python `hmac`):

| Secret | Timestamp | Body | `v1` signature |
| --- | --- | --- | --- |
| `whsec_test_0123456789abcdef0123456789abcdef` | `1763380800` | `{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid"}` | `029fe4509021b3db58be3326a42bc973e37369597e5c31fd92f8a64c3587f209` |
| `dev_webhook_secret_0000000000000000000000` | `1700000000` | `{"amount":"12.5","note":"café ☕"}` (UTF-8) | `4d09b42fe93ba66ee0d40e7aff67395927611909d7b21126ced3af625ca038d6` |
| `previous_secret_zzzzzzzzzzzzzzzzzzzzzzzzzzzz` | `1763380800` | *(empty)* | `4aeba15194eccfef7dfaf208ba56d5f99460256f6b5930ebd97fc83b20eee8e1` |

Check from a shell:

```bash
printf '%s' '1763380800.{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid"}' \
  | openssl dgst -sha256 -hmac 'whsec_test_0123456789abcdef0123456789abcdef'
# → 029fe4509021b3db58be3326a42bc973e37369597e5c31fd92f8a64c3587f209
```

## 3. Payload schema

JSON, snake_case, amounts as **decimal strings in USDC** (never numbers — no precision loss, NFR-1), timestamps
ISO 8601 with offset (UTC). The body is built when the event is created and stored as text, so retries and resends
send exactly the same bytes.

```json
{
  "id": "0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b",
  "type": "invoice.paid",
  "created_at": "2026-11-20T12:03:10.512+00:00",
  "api_version": "2026-11-17",
  "data": {
    "invoice": {
      "id": "0193a1b0-0000-7000-8000-000000000001",
      "external_order_id": "order-1001",
      "status": "paid",
      "amount": "10",
      "confirmed_total": "10",
      "remaining_amount": "0",
      "deposit_address": "0x8ba1f109551bd432803012645ac136ddd64dba72",
      "created_at": "2026-11-20T12:00:00+00:00",
      "expires_at": "2026-11-20T12:30:00+00:00",
      "metadata": { "customer": "42" }
    },
    "previous_status": "confirming"
  }
}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | uuid | Event ID (= `AcceptCrypto-Event-Id`). |
| `type` | string | Event type, see below. |
| `created_at` | datetime | When the change happened (not when it was delivered). |
| `api_version` | string | Payload schema version. Fields may be **added** within a version; receivers must ignore unknown fields. Renames/removals bump the version. |
| `data.invoice` | object | Snapshot of the invoice as of this event (below). |
| `data.previous_status` | string | Status events only: the status before the transition. |
| `data.transfer` | object | `payment.reverted` only: the transfer that disappeared from the chain. |

`data.invoice` — the snapshot. It contains the merchant's own data (external order ID, metadata) but **no secrets**:
no payment-page token, no idempotency key, no derivation index.

| Field | Type | Meaning |
| --- | --- | --- |
| `id` | uuid | Invoice ID. |
| `external_order_id` | string | The merchant's order ID. |
| `status` | string | Status after this event: `pending`, `confirming`, `underpaid`, `paid`, `overpaid`, `expired`, `paid_late`, `cancelled`. |
| `amount` | decimal string | Invoice amount. |
| `confirmed_total` | decimal string | Sum of confirmed transfers (on time and late) at this event. |
| `remaining_amount` | decimal string | `max(amount − confirmed_total, 0)`. |
| `deposit_address` | string | The invoice's deposit address (0x hex; compare case-insensitively). |
| `created_at`, `expires_at` | datetime | Invoice creation and payment deadline. |
| `metadata` | object (string → string) | The merchant's metadata, as given at creation. |

For status events, `status` and `confirmed_total` are the values of that transition (one operation can produce several
events, e.g. `underpaid` then `expired`). For `payment.reverted`, the snapshot is the invoice after the rollback.

`data.transfer` (`payment.reverted`):

| Field | Type | Meaning |
| --- | --- | --- |
| `tx_hash` | string | Transaction hash. |
| `log_index` | int | Log index of the `Transfer` event in the block. |
| `amount` | decimal string | Transfer amount. |
| `block_number`, `block_hash` | int, string | The block it had been seen in (now orphaned). |

**Event types:** `invoice.confirming`, `invoice.underpaid`, `invoice.paid`, `invoice.overpaid`, `invoice.expired`,
`invoice.paid_late`, `invoice.cancelled`, `invoice.pending` (back to pending after a reorg removed every unconfirmed
payment) and `payment.reverted` (followed by the resulting status event when the status changes, e.g. back to
`pending` or `underpaid`). Terminal outcomes the merchant acts
on: `invoice.paid`, `invoice.overpaid`, `invoice.paid_late`; `invoice.expired` may still be followed by
`invoice.paid_late`.

## 4. Delivery semantics

- **At least once.** A delivery can repeat (e.g. the merchant processed it but the response was lost, or a dispatcher
  died mid-request). Deduplicate by event ID (FR-16); the SDK helps with that.
- **Order.** Events of one invoice are delivered one after another in causal order: only the oldest *pending* event of
  an invoice is dispatched; while it is being retried, the newer ones wait. A **failed** event no longer blocks the
  queue. Events of different invoices are independent. The snapshot (`data.invoice.status`) is always the state as of
  that event, so a receiver can also compare `created_at` to ignore stale updates.
- **Retries (FR-14).** Exponential backoff with ±20 % jitter: `InitialRetryDelay · 2^(n−1)`, capped at
  `MaxRetryDelay`. The event is marked **failed** when `MaxAttempts` attempts were made or when the next attempt would
  fall outside `RetryWindow`, counted from the event's creation (or its last manual resend). Defaults:
  30 s, 1 m, 2 m, 4 m, 8 m, 16 m, 32 m, then hourly — 30 attempts over ~23 h within a 24 h window.
- **History (FR-19).** Every attempt (time, HTTP status or `null`, error, duration) is stored and shown in
  `GET /v1/invoices/{id}` → `events[].delivery_attempts`, together with `status` (`pending`/`delivered`/`failed`),
  `attempts`, `next_attempt_at`, `last_error`.
- **Manual resend (FR-15).** `POST /v1/events/{eventId}/resend` puts the event back to pending, due now, with a fresh
  retry budget and window. It keeps the **same event ID and the same body**; the history is kept. Works for delivered
  and failed events.
- **SSRF (NFR-6).** Before each attempt the URL host is resolved and every address must be public (unless the host is
  on `Webhooks:AllowedHosts`); the HTTP handler re-checks the address it connects to. A refused URL is a failed
  attempt (`Webhook URL refused: …`), no request is made.
- **Resilience (NFR-4).** A merchant that is down, slow or answering errors only schedules retries. A database outage
  makes the dispatcher pass fail; it is logged and retried with backoff (2 s doubling, max 1 min). Nothing is lost:
  events stay in the outbox.

### How the dispatcher works

`WebhookDispatcherService` (a `BackgroundService`) runs `WebhookDispatcher.RunOnceAsync` every `PollInterval`
(1 s), immediately again while passes find work. One pass:

1. **Claim** up to `BatchSize` due events in one statement:
   `WITH due AS (SELECT … WHERE status='Pending' AND next_attempt_at <= now AND <no older pending event of the invoice>
   ORDER BY id LIMIT n FOR UPDATE SKIP LOCKED) UPDATE outbox_events SET next_attempt_at = now + ClaimLease … RETURNING`.
   Parallel dispatchers (several service instances) skip each other's rows; the lease hides claimed rows while their
   HTTP requests run, without holding a transaction open. If a dispatcher dies, the lease (default 2 min, must exceed
   `RequestTimeout`) runs out and the event is claimed again.
2. **Send** the claimed events (up to `MaxParallelDeliveries` at once), signed with the current secret(s).
3. **Record** each result on the row (`xmin`-guarded, re-applied on a concurrent manual resend): delivered, or a
   failure with the next attempt time / failed.
4. Refresh the queue metrics (at most every 5 s when idle).

## 5. Secret rotation, end to end (FR-0)

The service always signs with `Merchant:WebhookSecret` (the **new** secret once you rotate). While
`Merchant:PreviousWebhookSecret` is set, it adds a second `v1=` entry made with the old secret, so the receiver can
switch at its own pace. The receiving side (SDK, FR-26) accepts a webhook if **any** configured secret matches
**any** `v1=` entry.

Rotation without losing a single webhook:

1. Generate a new secret (≥ 32 chars), e.g. `openssl rand -hex 32`.
2. **Service:** set `Merchant:PreviousWebhookSecret` = old secret, `Merchant:WebhookSecret` = new secret; restart.
   Webhooks now carry `v1=<new>,v1=<old>` — receivers that only know the old secret keep working.
3. **Merchant app (SDK):** configure both secrets (new + old), deploy. Then remove the old one.
4. **Service:** remove `Merchant:PreviousWebhookSecret`; restart. Only `v1=<new>` is sent.

The order of steps 2 and 3 does not matter: if the merchant app accepts both secrets first, step 2 can even skip
`PreviousWebhookSecret`. Retries and resends are signed at send time, so events queued before the rotation are
signed with the new secret too. Secrets come from configuration / environment / user-secrets only and are never
logged (NFR-5).

## 6. Configuration

| Setting | Default | Demo / Development profile |
| --- | --- | --- |
| `Webhooks:InitialRetryDelay` | `00:00:30` | `00:00:05` |
| `Webhooks:MaxRetryDelay` | `01:00:00` | `00:01:00` |
| `Webhooks:MaxAttempts` | `30` | `30` |
| `Webhooks:RetryWindow` | `1.00:00:00` (24 h) | `00:10:00` |
| `Webhooks:RequestTimeout` | `00:00:10` | — |
| `Webhooks:DispatcherEnabled` | `true` | — |
| `Webhooks:PollInterval` | `00:00:01` | — |
| `Webhooks:BatchSize` | `20` | — |
| `Webhooks:MaxParallelDeliveries` | `4` | — |
| `Webhooks:ClaimLease` | `00:02:00` (must be > `RequestTimeout`) | — |
| `Webhooks:DispatcherErrorDelay` | `00:00:02` | — |
| `Webhooks:AllowedHosts` | `[]` | `["localhost"]` (compose: `demo-shop`) |

The demo profile lives in `appsettings.Development.json`; in Docker use environment variables, e.g.
`Webhooks__InitialRetryDelay=00:00:05`, `Webhooks__RetryWindow=00:10:00`.

## 7. Metrics (NFR-7)

Meter `AcceptCrypto.Webhooks`:

| Instrument | Kind | Meaning |
| --- | --- | --- |
| `acceptcrypto.webhooks.queue_length` | gauge | Pending events (due or waiting for a retry) — the webhook queue length. |
| `acceptcrypto.webhooks.due` | gauge | Pending events due now (a growing value means the dispatcher is behind). |
| `acceptcrypto.webhooks.failed` | gauge | Events that exhausted their retries (need a manual resend). |
| `acceptcrypto.webhooks.oldest_pending_age` | gauge, s | Age of the oldest pending event. |
| `acceptcrypto.webhooks.attempts` | counter, tag `outcome` = `delivered`/`retry`/`failed` | Delivery attempts. |
| `acceptcrypto.webhooks.attempt_duration` | histogram, s | Attempt duration. |

Logs: `Webhook {EventId} ({EventType}) delivered …` (Information), `… attempt {n} failed: {Error}; next attempt at …`
(Warning), `… marked failed after {n} attempts` (Error). No secrets, URLs only as configured origin at startup.
