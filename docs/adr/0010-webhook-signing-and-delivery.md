# ADR 0010 — Webhook signing and delivery

- **Status:** Proposed (default)
- **Date:** 2026-11-15
- **Spec:** FR-12 … FR-16, FR-26, FR-27, NFR-4, NFR-6

## Decision

**Event envelope** (JSON, `snake_case`):

```json
{
  "id": "evt_01JD…",            // unique, stable across retries and manual resends (FR-16)
  "type": "invoice.paid",
  "created_at": "2026-11-15T10:00:00Z",
  "data": { "invoice": { "id": "…", "status": "paid", "previous_status": "confirming",
            "amount": "25.00", "amount_paid": "25.00", "order_id": "…", "metadata": {} } }
}
```

Event types: `invoice.created`, `invoice.<status>` for every transition (FR-12), plus
`invoice.payment_received` for confirmed payments that don't change the status
([0005](0005-inexact-payment-rules.md) rule 7).

**Signature (FR-13)** — Stripe-style, so integrators recognise it:

- Header `AcceptCrypto-Signature: t=<unix-seconds>,v1=<hex HMAC-SHA256(secret, "<t>.<raw body>")>`.
- Header `AcceptCrypto-Event-Id: <id>` for cheap deduplication.
- The SDK verifies with a constant-time comparison, rejects timestamps older/newer than a
  tolerance (default 5 min) to stop replays, and exposes the event ID so the merchant can
  deduplicate (FR-26). Multiple `v1=` values are accepted to allow secret rotation.
- Signing secret: generated per merchant, stored in configuration/secrets, never logged.

**Delivery (FR-14, FR-15)**

- From the outbox ([0006](0006-storage-and-background-processing.md)), `POST` with a 10 s
  timeout. 2xx = delivered; anything else (incl. timeout, DNS, TLS errors) = failed attempt.
- Retry schedule with ±20 % jitter: 10 s, 1 min, 5 min, 30 min, 2 h, 6 h, 12 h, 24 h
  (8 attempts, ~45 h). After the last attempt the event is `Failed` (metric + log).
  The schedule is configurable (the demo uses a shorter one).
- Every attempt is stored (time, HTTP status, duration, truncated response body) and shown in
  invoice details (FR-19).
- `POST /v1/events/{id}/resend` re-queues an event with the **same** event ID (FR-15).
- Per-invoice ordering is best-effort; each payload carries the full current invoice state and
  `previous_status`, so consumers can act on the latest state.

**SSRF (NFR-6):** webhook URL must be `https` (http allowed only in the `Local` profile); the
host is resolved at send time and private, loopback, link-local and metadata ranges are refused
(checked on the actually connected IP via `SocketsHttpHandler.ConnectCallback`, which also
defeats DNS rebinding). Redirects are not followed.
