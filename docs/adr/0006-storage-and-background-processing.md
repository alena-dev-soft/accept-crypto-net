# ADR 0006 — PostgreSQL + EF Core, transactional outbox, BackgroundService workers

- **Status:** Proposed (default)
- **Date:** 2026-11-15
- **Spec:** NFR-2, NFR-3, NFR-4, NFR-8, NFR-9, FR-8, FR-14

## Context

Status changes and their webhooks must be atomic (NFR-2), parallel processing must not race
(NFR-3), and setup must stay "one command" (NFR-9). A message broker (RabbitMQ/Kafka) or a job
framework (Hangfire/Quartz) would show familiarity with tools but add moving parts.

## Decision

- **PostgreSQL 16 + EF Core 8 (Npgsql).** Migrations in `AcceptCrypto.Infrastructure`.
  Amounts as `numeric(78,0)` base units ([0011](0011-money-representation.md)).
- **Transactional outbox.** Every invoice transition writes an `outbox_events` row (unique
  `event_id`, type, JSON payload) in the **same** `SaveChanges` transaction as the status
  change. No status without an event, no event without a status (NFR-2).
- **Workers** are plain `BackgroundService`s in `AcceptCrypto.Service`:
  - `ChainIndexer` — reads logs, records payments, applies transitions ([0003](0003-chain-reading.md)).
  - `InvoiceExpiryWorker` — applies the expiry rule ([0005](0005-inexact-payment-rules.md)).
  - `WebhookDispatcher` — claims due outbox rows with `SELECT … FOR UPDATE SKIP LOCKED`,
    delivers, records each attempt, schedules retries ([0010](0010-webhook-signing-and-delivery.md)).
- **Concurrency (NFR-3).** Invoices carry an optimistic concurrency token (PostgreSQL `xmin`).
  Transitions are applied by loading the invoice, calling the domain method, saving; on
  `DbUpdateConcurrencyException` the unit of work is reloaded and retried. The indexer is a
  single instance (advisory lock), so the main race is indexer vs. expiry worker vs. API —
  exactly what the concurrency token covers.
- **Idempotent API create (FR-2).** `Idempotency-Key` header stored with a unique index and a
  hash of the request body; same key + same body → the original response; same key +
  different body → `409 Conflict` (Problem Details).
- **Integration tests** use Testcontainers (PostgreSQL) and Anvil, so they hit a real DB and a
  real chain (NFR-8). Docker-less environments run the unit tests only (tests are categorised).

## Consequences

- One database, no broker: the stack is `postgres + anvil + service + demo shop`.
- The outbox gives at-least-once delivery; merchants deduplicate by event ID (FR-16), which
  the SDK makes easy.
- Throughput is bounded by a single DB — more than enough for the MVP and the demo.
