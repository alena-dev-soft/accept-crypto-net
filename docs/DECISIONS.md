# Decision log — AcceptCrypto.NET

Decisions taken on the open questions of the spec (`AcceptCrypto.NET_SPEC.md`, rev. Nov 16, 2026).
Each entry: the decision, why, what it costs, and what is left for the roadmap.
New decisions are appended with the next number; a changed decision is marked *Superseded by Dn*, not deleted.

| # | Topic | Decision (short) |
| --- | --- | --- |
| D1 | Payment ↔ invoice linking | Unique deposit address per invoice, derived from the merchant's xpub (BIP32, non-hardened) |
| D2 | Chains and token | Local: Anvil + mock 6-decimal USDC + pre-funded payer. Public demo: Base Sepolia + Circle test USDC |
| D3 | Payment detection | The service reads the chain itself: `eth_getLogs` over ERC-20 `Transfer` + persisted checkpoint |
| D4 | Confirmations | `confirming` status exists; N is configurable, small default for an L2 |
| D5 | Inexact payments | Tolerance 0; partial transfers add up; late payment → `paid_late`; transfers to closed invoices → unmatched |
| D6 | Cancellation | Merchant can cancel a `pending` invoice → `cancelled` (terminal, emits an event) |
| D7 | Storage / background work | PostgreSQL + transactional outbox + `BackgroundService` workers, no broker |
| D8 | Payment page | Instructions + QR code, no wallet connection |
| D9 | Deployment | Local (one command) by default; public testnet optional, following a guide |
| D10 | Solution layout & tooling | .NET 10, layered projects, central package management, warnings as errors |
| D11 | Domain model & edge cases | BigInteger base units; status derived from facts, walked along diagram edges; `expired → confirming` for on-time payments found late; late overpay → `paid_late` |

---

## D1 — Payment ↔ invoice linking: one deposit address per invoice (xpub-derived)

**Decision.** Every invoice gets its own deposit address. Addresses are derived from an extended public key
(xpub) supplied by the merchant via configuration (FR-0), using BIP32 **non-hardened** derivation:
`<xpub>/<index>`, where `index` = the invoice's sequence number (monotonic, allocated by the database,
never reused). The Ethereum address is the Keccak-256 of the derived public key (last 20 bytes, EIP-55 checksum).

- The service holds **no private keys** (NFR-5). The merchant derives the matching private keys in their own
  wallet from the corresponding xprv/seed when they want to move the funds.
- Matching is unambiguous (FR-5): a `Transfer` to address *A* belongs to the invoice that owns *A*, regardless
  of the amount or how many invoices are open.
- Partial, over- and late payments match naturally (FR-11), because matching does not depend on the amount.
- Plain ERC-20 transfers from any wallet work; the payment page needs only an address, amount and QR (D8).

**Cost.** Funds are spread over many addresses; sweeping them is out of MVP scope (no keys in the service).
Each address would need gas to move USDC out — documented, not solved.
Index gaps (e.g. a rolled-back insert) are harmless: an unused address simply never receives funds.

**Roadmap.** CREATE2 forwarder contracts (deterministic per-invoice addresses that can be swept to one
merchant address by a contract call, without per-address gas funding).

## D2 — Chains and token

**Decision.**
- **Local (default):** Anvil with a mock ERC-20 "USDC" with **6 decimals** and a pre-funded payer account.
  Used for the one-command stack, the Definition of done and integration tests. No wallet, faucet or real gas.
- **Public demo (optional):** Base Sepolia + Circle's test USDC, paid from MetaMask (video / public demo).

Chain ID, RPC URL, token contract address and token decimals are configuration, not code.
One chain and one token at a time (MVP scope).

## D3 — Payment detection: the service reads the chain itself

**Decision.** A background scanner polls `eth_getLogs` for the token contract's ERC-20
`Transfer(address,address,uint256)` events, filtered by `to` ∈ the set of watched deposit addresses
(or filtered in-process if the set is large). Progress is stored in a **persisted checkpoint**
(last scanned block number + hash) so the scanner resumes after downtime (FR-9).

- Scanning is done in **chunks** with a configurable maximum block range, to respect RPC provider limits.
- A transfer is identified by `(chain_id, tx_hash, log_index)` with a unique constraint → never counted twice,
  including after restarts (FR-8).
- Reorg handling (FR-10): stored block hashes are compared against the canonical chain for the unconfirmed
  window; transfers from blocks that are no longer canonical are marked reverted, the invoice is
  recalculated and a `payment.reverted` event is emitted.

No external indexer/webhook provider (Alchemy Notify, etc.) — reading the chain is the point of the project.

## D4 — Confirmations and the `confirming` status

**Decision.** The `confirming` status exists (as in the spec's state diagram). A transfer is final when
`head - block_number + 1 >= N`. **N is configurable**; default is small for an L2 (proposed default: 3 for
Base Sepolia, 2 for local Anvil so demos are fast — exact defaults are set when the scanner is implemented).
Only confirmed transfers count towards `paid` / `overpaid` / `underpaid` totals.

## D5 — Inexact payments

**Decision.**
- **Amount tolerance = 0.** The confirmed total must equal the invoice amount exactly, in the token's
  6 decimals (amounts are stored as integer base units — NFR-1).
- **Partial transfers add up.** Multiple transfers to the invoice's address are summed:
  total < amount → `underpaid` (still open until the deadline); total = amount → `paid`;
  total > amount → `overpaid`.
- **Payment after the deadline** follows the spec's status table: a payment is on time if its **block
  timestamp** is before the deadline (even if confirmations complete later). An `expired` invoice whose
  confirmed total reaches the amount with late transfers becomes `paid_late`.
- **Transfers to a closed invoice** (terminal status: `paid`, `overpaid`, `paid_late`, `cancelled`) are
  recorded as **unmatched payments** (FR-11a) and listed via the API for manual reconciliation, together
  with transfers to unknown addresses.

## D6 — Cancellation

**Decision.** Included in the MVP. The merchant can cancel an invoice via the API **only while it is
`pending`** (no payment seen). The invoice moves to a new status **`cancelled`**: terminal, produces an
`invoice.cancelled` event (FR-12). Cancelling in any other status returns a 409 Problem Details response.
A transfer arriving at a cancelled invoice's address is recorded as unmatched (D5).

This adds one row to the spec's status table:

| Status | Meaning | Terminal |
| --- | --- | --- |
| `cancelled` | Cancelled by the merchant while pending | yes |

## D7 — Storage and background processing

**Decision.** PostgreSQL via EF Core; a **transactional outbox**: the invoice status change and the
outbox row for its webhook event are written in the same DB transaction (NFR-2). `BackgroundService`
workers: chain scanner, confirmation/expiry processor, webhook dispatcher (retries with exponential
backoff, FR-14). Concurrency on one invoice is controlled with optimistic concurrency (row version)
and/or row locks (NFR-3). **No message broker** — keeps the one-command setup small.

Integration tests use Testcontainers (PostgreSQL, Anvil) on a real Docker daemon (CI runs on
`ubuntu-latest`, which provides Docker).

## D8 — Payment page

**Decision.** A simple server-rendered public page addressed by an unguessable invoice token (FR-29):
network, token, address, exact amount, deadline, a QR code and live status (polling or SSE, FR-30).
**No browser wallet connection** in the MVP (roadmap). The UI technology is chosen when the page is built
(kept lightweight, rendered by the service itself).

## D9 — Deployment

**Decision.** Local by default: the whole stack (service, PostgreSQL, Anvil, demo shop) starts with one
command (NFR-9). A public testnet instance is optional and described in a step-by-step guide
(Base Sepolia RPC, Circle test USDC, merchant xpub, MetaMask). No hosted public instance is part of the MVP.

## D10 — Solution layout and tooling (this step)

- **.NET 10** (`global.json` pins SDK 10.0.x with `latestFeature` roll-forward).
- Projects (`src/`): `AcceptCrypto.Service` (ASP.NET Core: API, workers, payment page),
  `AcceptCrypto.Domain` (pure domain logic, no infrastructure dependencies),
  `AcceptCrypto.Infrastructure` (EF Core/PostgreSQL, blockchain RPC; depends on Domain),
  `AcceptCrypto.Sdk` (NuGet package **`AcceptCrypto.NET`**, net10.0), `AcceptCrypto.DemoShop`
  (references only the SDK), `AcceptCrypto.Payer` (console, assembly name `pay`).
- Tests (`tests/`): `AcceptCrypto.Domain.Tests`, `AcceptCrypto.Sdk.Tests`, `AcceptCrypto.IntegrationTests` (xUnit).
- `Directory.Build.props`: nullable, implicit usings, **warnings as errors**, .NET analyzers
  (`AnalysisMode=Recommended`), code style enforced in build. `Directory.Packages.props`: central package versions.
- Solution file: `AcceptCrypto.NET.slnx` (the new XML solution format, default in .NET 10).

## D11 — Domain model and state-machine edge cases (step 2)

**Money.** `UsdcAmount` stores integer **base units** in a `BigInteger` (6 decimals, bounded by `uint256`
when parsed or built from chain data). Parsing is strict and culture-free: ASCII digits, no sign/whitespace/
exponent/group separators, no leading zeros (except `0`), optional `.` + 1–6 digits. `"1.0000000"` is rejected
(7 decimals) even though it is numerically exact. Canonical output trims trailing zeros (`"10.5"`, `"3"`);
`ToFixedString()` gives `"10.500000"`. One token per deployment (MVP), so the type is USDC-specific rather than
a generic (amount, decimals) pair; a different-decimals token is a roadmap item.

**How status is computed.** Operations change facts (record / confirm / revert a transfer, deadline check,
cancel); the invoice then derives the target status and walks to it **only along diagram edges**, emitting one
`InvoiceStatusChanged` event per edge. Target status:
1. any unconfirmed **on-time** transfer → `confirming`;
2. else on-time confirmed total `>` amount → `overpaid`, `=` amount → `paid` (tolerance 0);
3. else, before the deadline → `pending` (nothing confirmed) / `underpaid`;
4. else (deadline passed) → `paid_late` if confirmed total incl. late transfers `>=` amount, otherwise `expired`.

The deadline counts as passed when `now >= ExpiresAt`, or the invoice was already expired (sticky — immune to
clock skew), or a non-reverted transfer has a block timestamp `>= ExpiresAt` (chain evidence). A block timestamp
exactly at the deadline is **late**.

**Edge cases resolved here (not in the spec):**

| Case | Decision |
| --- | --- |
| Overpayment after the deadline (late transfers push the total above the amount) | `paid_late`; the excess is visible as `ExcessAmount` / `ConfirmedTotal` (no separate status) |
| Partial late payment into `expired` | Recorded and summed; invoice stays `expired` until the total reaches the amount → `paid_late` |
| Late transfer seen (unconfirmed) on an `expired` invoice | No status change, no `confirming` (diagram has no `expired → confirming` for late money); `paid_late` once confirmed |
| On-time transfer (block ts < deadline) discovered only after the invoice was marked `expired` (scanner lag / downtime) | **New edge `expired → confirming`**, then the normal `paid`/`overpaid`/`underpaid` path. The spec's "on time by block timestamp" rule wins. The expiry worker should also avoid this by expiring only once the scanner has caught up past the deadline (step 6) |
| Deadline passes while `confirming` | No change; the invoice waits for confirmations. If the result is short: `confirming → underpaid → expired` (two events) |
| Reorg after the deadline removes everything | `payment.reverted`, then `confirming → pending → expired` |
| Late transfer arrives while still `confirming` (before the on-time one confirms) | Accepted (invoice not terminal). After the invoice turns `paid`, it can still confirm/revert: no status change, excess visible |
| Transfer to a terminal invoice (`paid`, `overpaid`, `paid_late`, `cancelled`) | `RecordTransfer` returns `InvoiceClosed`; the caller stores it as an unmatched payment (FR-11a) |
| Same transfer (tx hash + log index) again | `Duplicate`, no change — also on terminal invoices (checked before "closed") |
| A reverted transfer re-included in a new block (same tx hash + log index) | Reinstated with the new block data, back to "seen" |
| Revert of a **confirmed** transfer | Rejected (`transfer_state_conflict`): N confirmations = final (D4). Choose N so deeper reorgs are not a practical risk |
| Zero-amount transfer | Rejected by `ChainTransfer` (address-poisoning spam); the scanner drops them |
| Cancel a `pending` invoice whose deadline passed but which the worker has not expired yet | Allowed (status is still `pending`) |
| `invoice.created` event | Not emitted: creation is the merchant's own synchronous API call; events are for status changes (FR-12) |

**Events.** `InvoiceStatusChanged` (type `invoice.<status>`) and `PaymentReverted` (`payment.reverted`), each with
a UUIDv7 `EventId`. One operation may return several events; their list order is the causal order and the outbox
(step 3) must preserve it (e.g. an identity column), since UUIDv7s from the same millisecond are not guaranteed monotonic.
`payment.reverted` is emitted for every reverted transfer, also on terminal invoices, before any status event.

**Payment page token.** 32 bytes from `RandomNumberGenerator`, base64url (43 chars).
**Deposit address** is validated and stored lower case; EIP-55 casing is presentation (step 5).
**Clock.** `Invoice.Create` takes a `TimeProvider`; operations take `now` explicitly, so the caller decides which
clock applies (tests use `FakeTimeProvider`).

## D12 — Persistence, outbox, idempotency and concurrency (step 3)

**Stack.** EF Core 10 + Npgsql, snake_case names (`EFCore.NamingConventions`), migrations in
`src/AcceptCrypto.Infrastructure/Persistence/Migrations` (local tool `dotnet-ef`, manifest `dotnet-tools.json`).
Repositories are singletons built on `IDbContextFactory`: every call uses its own short-lived context, so they are
thread-safe and usable from API requests and `BackgroundService` workers alike.

**Tables.** `invoices` (+ `version` concurrency token), `observed_transfers` (owned by the invoice aggregate,
unique `(chain_id, tx_hash, log_index)` — FR-8), `unmatched_payments` (same unique key, FR-11a),
`scanner_checkpoints` (one row per chain, block number + hash — FR-9), `outbox_events` + `webhook_delivery_attempts`
(FR-14/15/19), `idempotency_keys` (FR-2), sequence `invoice_derivation_index_seq` (BIP32 child index = invoice number).

**Money (NFR-1).** `numeric(78,0)` in token base units (fits uint256), mapped `UsdcAmount ↔ BigInteger`; no
floating point anywhere. Check constraints keep invoice/transfer amounts positive.

**Concurrency (NFR-3): optimistic concurrency with retry**, not `SELECT … FOR UPDATE`.
- `invoices.version` is an explicit integer concurrency token. The DbContext bumps it on **every** change of the
  aggregate — also when only a child transfer row is added/changed and the invoice row itself is unchanged. Without
  that, two workers confirming two different transfers of one invoice would both commit on stale totals and the
  invoice would never reach `paid` (test: `ConcurrencyTests.Parallel_confirmations_settle_to_the_correct_final_status`).
- `IInvoiceRepository.UpdateAsync(id, mutate)` is the unit of work: load → apply the domain operation → save; on
  `DbUpdateConcurrencyException` (or a unique violation from a parallel insert of the same transfer) it reloads and
  re-applies, up to `Persistence:MaxConcurrencyRetries` (default 5). Domain operations are pure state transitions,
  so re-running them on fresh state is safe.
- Why not row locks: contention per invoice is rare (scanner, expiry worker, API cancel), optimistic checks keep
  transactions short and lock-free, and work the same through EF without raw SQL. Row locks stay an option for the
  dispatcher claiming outbox rows (`FOR UPDATE SKIP LOCKED`, step 7).
- `outbox_events` uses PostgreSQL `xmin` as its row version (single-row updates by the dispatcher/API).

**Outbox (NFR-2).** `UpdateAsync` adds one `outbox_events` row per domain event to the same context, so a single
`SaveChanges` — one transaction — writes the invoice row, its transfers and the events. The identity column `id`
keeps the causal order of events from one operation. The payload is the final webhook JSON
(`{"id","type","created_at","data":{...}}`, snake_case, amounts as decimal strings), stored as `text` so the
exact bytes are signed and resent (FR-15 keeps event ID and payload). Status/attempts/`next_attempt_at`/
`delivered_at`/`last_error` on the row, every HTTP attempt in `webhook_delivery_attempts`. A manual resend resets
the retry budget, keeps the history.

**Idempotency (FR-2).** `CreateAsync(key, fingerprint, factory)` runs in one transaction:
`INSERT INTO idempotency_keys … ON CONFLICT DO NOTHING`. A concurrent request with the same key blocks on the
uncommitted row and, after the first commits, sees the conflict and returns the original invoice (`Replayed`), or
`KeyReusedWithDifferentRequest` if the request fingerprint differs (the API maps that to 409/422 in step 4). If the
first request fails (validation), its key row is rolled back and the key can be reused. Keys are kept forever
(single merchant, small volume). `nextval` gaps after rollbacks only mean unused deposit addresses.

**Chain ID.** `Persistence:ChainId` (default 31337, Anvil) is stamped on stored transfers, unmatched payments and the
checkpoint. Step 6 should source it from the chain configuration (one setting).
