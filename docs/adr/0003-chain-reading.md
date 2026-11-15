# ADR 0003 — The service reads the chain itself (Nethereum `eth_getLogs` polling + block cursor)

- **Status:** Proposed (default)
- **Date:** 2026-11-15
- **Spec:** FR-6, FR-7, FR-8, FR-9, FR-10, NFR-4, NFR-7

## Context

The service must detect payments on its own, survive downtime, never double count and never
report a false "paid" after a reorg. Options: (a) third-party webhooks (Alchemy Notify, Moralis
Streams, …), (b) WebSocket `eth_subscribe`, (c) polling `eth_getLogs` with our own cursor.
(a) is simpler but hides exactly the part the portfolio has to prove and adds a vendor account
to the setup. (b) loses events on disconnect and still needs (c) to catch up.

## Decision

A `ChainIndexer` `BackgroundService` polls with Nethereum:

1. `head = eth_blockNumber`. `safe = head − N` (N = confirmations, [0004](0004-confirmations.md)).
2. **Confirmed scan** — `eth_getLogs(address = router, topic0 = PaymentReceived, from = cursor+1,
   to = min(safe, cursor + MaxBlockRange))`. Logs read here are *final*. In one DB transaction:
   upsert the payments as `Confirmed`, mark previously `Detected` payments in that range that are
   **not** in the result as `Reorged`, apply the invoice transitions, write the outbox events and
   move the persisted cursor (`chain_cursor` table) to `to`. Either all of it commits or none.
3. **Tentative scan** — `eth_getLogs(from = safe+1, to = head)`. New logs are stored as
   `Detected` (with block number, block hash, block timestamp) and drive the `Confirming` status.
   They are never counted towards "paid".
4. Sleep `PollInterval` (default 2 s on Base Sepolia), repeat. Ranges are chunked by
   `MaxBlockRange` (default 500) to respect provider limits.

Guarantees:

- **No double counting (FR-8):** unique index on `(chain_id, tx_hash, log_index)`; inserts are
  upserts. Re-scanning a range after a crash is idempotent.
- **Catch-up after downtime (FR-9):** the cursor is persisted in the same transaction as the
  effects; on start the indexer resumes from `cursor + 1` and walks forward in chunks until it
  reaches `safe`. Initial cursor = configured `StartBlock` (router deployment block).
- **Reorg safety (FR-10):** only logs at depth ≥ N ever become `Confirmed`, and they are re-read
  at that depth rather than promoted from the tentative scan. A reorg shallower than N can only
  move an invoice from `Confirming` back to its previous status. A reorg deeper than N is outside
  the trust model; it is detected (block hash of a confirmed payment no longer matches) and
  raised as a critical log + metric for manual reconciliation, never silently "fixed".
- **RPC outages (NFR-4):** RPC calls go through a Polly pipeline (timeout, retry with jitter,
  circuit breaker). While the RPC is down the cursor simply does not move; nothing is lost.
  Optional fallback RPC URL.
- **Observability (NFR-7):** metrics `acceptcrypto_chain_head`, `acceptcrypto_chain_cursor`,
  `acceptcrypto_chain_lag_blocks`; health check is *Degraded* when lag exceeds a threshold.

## Consequences

- Shows the real .NET ↔ blockchain bridge: ABI event decoding, block ranges, reorgs, cursors.
- Detection latency ≈ poll interval; finality latency ≈ N × block time.
- Only one indexer instance may run per chain; enforced with a PostgreSQL advisory lock so a
  second replica stays passive (safe for the public demo with rolling deploys).
