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
| D13 | Local chain & derivation | Own minimal 6-decimal MockUsdc (artifact committed), deployed by the .NET payer at a fixed address; xpub at m/44'/60'/0'/0, child = invoice index; payer reads a public payment-details endpoint |
| D18 | SDK | Package `AcceptCrypto.NET`, net10.0, no 3rd-party deps; `AddAcceptCrypto()` + typed HttpClient; Problem Details → typed exceptions; own `UsdcAmount`; constant-time HMAC over all secrets × all `v1=`; `MapAcceptCryptoWebhook` with pluggable dedup (in-memory default) |

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
(kept lightweight, rendered by the service itself). → Chosen in step 8, see **D17**.

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

## D13 — Local chain, mock USDC, xpub derivation and the scripted payer (step 4 of the plan: local chain)

**Mock token.** `contracts/src/MockUsdc.sol` is an own minimal ERC-20 (6 decimals, open `mint` = test faucet, no
external dependencies), compiled with solc 0.8.24 (optimizer 200, EVM cancun, no metadata hash). The artifact
(ABI + creation bytecode) is committed as `src/AcceptCrypto.LocalChain/Contracts/MockUsdc.json` and embedded in the
`AcceptCrypto.LocalChain` assembly, so **.NET users never need Foundry/solc**: `pay setup` deploys it via Nethereum.
`scripts/build-contracts.sh` (forge + jq) regenerates the artifact when the Solidity changes.

**Fixed token address.** The deployer is Anvil account #0 and the deployment is its first transaction on a fresh
chain, so the token is always at `0x5FbDB2315678afecb367f032d93F642f64180aa3` (`AnvilAccounts.MockUsdcAddress`).
The compose stack (step 11) can therefore configure the service with a constant. `pay setup` is idempotent (deploys
only if there is no code at that address, then tops the payer up to 1,000,000 USDC) and fails clearly if the
deployer was already used on that chain.

**Accounts (TEST ONLY, public keys of Anvil's default mnemonic).** #0 deployer, #1 payer (pre-funded with ETH by
Anvil, USDC by `pay setup`), #2 spare. The payer refuses to use the well-known key on any chain other than 31337.

**Derivation.** `XpubDepositAddressDeriver` (Infrastructure, NBitcoin via Nethereum.HdWallet + Nethereum Keccak):
the configured key (`Merchant:Xpub`) is the extended public key at **`m/44'/60'/0'/0`** (the "external chain" of the
first Ethereum account, as used by MetaMask/Ledger Live); the deposit address of invoice index *i* is `xpub/i`,
i.e. the wallet's account `m/44'/60'/0'/0/i`, so the merchant can import the same seed and see/move the funds.
Only non-hardened indexes (0 … 2^31-1) are possible; an `xprv`/`tprv` is rejected without echoing it (NFR-5).
Both `xpub` and `tpub` serialisations are accepted. The xpub is parsed lazily on first use, so the service starts
without merchant configuration (Preview `/health`).
Local/test merchant wallet: the public BIP39 test mnemonic `abandon … abandon about` →
`xpub6EF8jXqF…CyPeBFFr` (in `appsettings.Development.json`). **Test only: anyone can spend from these addresses.**
Reference values in the tests come from an independent implementation (`cast wallet address --mnemonic-index`).

**Payer ↔ service contract (to be implemented by the API/payment-page steps).** The payer resolves `<invoice>` as:
- a payment page URL `http(s)://host[/prefix]/pay/{token}` → `GET {host[/prefix]}/api/public/invoices/{token}`;
- a GUID → merchant API `GET /v1/invoices/{id}` with header `X-Api-Key`;
- anything else → a public token on `--service` (default `http://localhost:5052`).

The JSON it reads (snake_case, the FR-4 payment details): required `address` (or `deposit_address`) and `amount`
(decimal string); optional `chain_id` (checked against the RPC chain), `token_address` (overrides the default token),
`status`, `expires_at`; the fields may also be nested under `payment`. If the API steps choose other routes or
names, adjust `InvoiceReference`/`PaymentDetailsClient` in the payer (unit tests in `PayerLogicTests`).

**Reorg / time helpers.** `AnvilClient.ReorgAsync(depth)` uses `anvil_reorg(depth, [])`: the last *depth* blocks are
replaced by blocks of the same height with new hashes and **without** the original transactions (verified: the
transfer's receipt disappears, the balance goes back). `ReorgToSnapshotAsync` is the `evm_snapshot`/`evm_revert`
alternative. Time warp = `evm_increaseTime` + mine (or `evm_setNextBlockTimestamp`); this moves **block
timestamps** (the on-time rule), while the service's own clock is a `TimeProvider` that tests fake separately.

## D14 — Chain scanner: detection, confirmations, reorgs, unmatched, expiry (step 6)

Code: `src/AcceptCrypto.Infrastructure/Chain/Scanning`. Configuration section `Chain` (`ChainOptions`).

**Log filter: `eth_getLogs` filtered on the recipient topic, not in-memory filtering.** The filter is
`address = token, topics = [Transfer, null, [to₁ … toₖ]]`, with the deposit addresses OR-ed in batches of
`MaxAddressesPerQuery` (default 100). Why: on a public testnet/mainnet the USDC contract emits thousands of transfers
per block range that are not ours; filtering on the node keeps responses tiny and the block range per request large,
which matters with free-tier RPC limits. In-memory filtering only wins with a huge address set — then the batches
multiply. With one merchant and a modest number of invoices the filter is the better trade-off; the roadmap item is to
stop watching addresses of invoices closed longer than a retention window (today **all** issued addresses are watched,
because late money to a closed invoice must be recorded as unmatched, FR-11a). Consequence: transfers to addresses that
are not deposit addresses are never seen, so `UnmatchedReason.UnknownAddress` is unreachable with this filter (kept for
an in-memory variant).

**No missed invoices.** Each pass reads the head *first* and the address list *second*. An invoice missing from the
list was created after the head was read, so its deposit address can only receive money in a later block, scanned in
a later pass.

**Pass algorithm** (`ChainScanner.RunPassAsync`; the `ChainScannerService` loops it):
1. Verify `eth_chainId` = `Chain:ChainId` once per process (refuses to scan the wrong chain).
2. Head → checkpoint check: the stored hash of the checkpoint block is compared with the canonical one. Mismatch (or
   the chain got shorter) = reorg → checkpoint rolled back by `ReorgRollbackDepth` (default 32) and persisted.
3. Unconfirmed transfers (state Seen): for each, a **fresh** `eth_getBlockByNumber` of its block. Different hash →
   `RevertTransfer` (domain emits `payment.reverted` + the status edge back) and the next scan starts
   `ReorgRollbackDepth` below that block, so a transaction re-included in another block is found again and reinstated.
   Same hash and `head - block + 1 >= N` → `ConfirmTransfer`. A missing block (lagging node) or a block above the head
   is *not* treated as a reorg: only positive evidence reverts, and nothing is confirmed without a matching hash.
4. Scan `[checkpoint+1, head]` in chunks of `MaxBlockRange` (default 500); if the node rejects a range with a JSON-RPC
   error (range/result limits), the range is halved down to one block. Per log: skip `removed` and zero-value logs,
   fetch the block (timestamp + hash must match the log's block hash, otherwise abort the pass and retry), build a
   `ChainTransfer`, find the invoice by deposit address, `RecordTransfer` — and, if the transfer is already N deep
   (catch-up after downtime), `ConfirmTransfer` **in the same transaction**. `InvoiceClosed` → unmatched payment with
   reason `InvoiceClosed` and the invoice ID. The checkpoint (block + hash, hash read *before* the logs) is saved after
   every chunk, so a crash loses at most one chunk of work, which is redone idempotently.

**Idempotency (FR-8).** The domain returns `Duplicate` for a known (tx hash, log index); the DB has unique
`(chain_id, tx_hash, log_index)` on `observed_transfers` and on `unmatched_payments` (`ON CONFLICT DO NOTHING`).
Re-scanning any range — after a restart, a lost checkpoint or a reorg rollback — changes nothing. Every status change
goes through `IInvoiceRepository.UpdateAsync` (invoice + outbox in one transaction, optimistic concurrency, NFR-2/3).

**Confirmations (FR-7, D4).** Final when `head - block + 1 >= N`. Defaults: `Confirmations = 2` (local Anvil, fast
demos); use 3+ on Base Sepolia. A confirmed transfer is final (D11): choose N above any reorg depth you expect.
**On time** = block timestamp < `expires_at` (domain rule, D11).

**Expiry worker.** `InvoiceExpiryProcessor` (looped by `InvoiceExpiryService` every `ExpiryInterval`) expires
pending/underpaid invoices whose deadline is not after `ChainScanState.ScannedThrough` = max(timestamp of the last
scanned block, the service time at which a pass read the head and then caught up to it). So nothing expires before the
scanner has looked at the blocks up to the deadline (e.g. right after a restart); an on-time payment found later still
goes `expired → confirming` (D11). Caveat: Anvil without `--block-time` mines no blocks when idle — then the
"caught up" time is what moves `ScannedThrough` forward.

**Resilience (NFR-4).** Both workers catch everything except shutdown, log it and retry with exponential backoff
(`RetryBaseDelay` doubling to `RetryMaxDelay`, ±20 % jitter); the host never stops. All RPC failures surface as
`ChainRpcException` with messages that never contain the RPC URL (it may hold a provider API key, NFR-5).

**Observability (NFR-7).** Meter `AcceptCrypto.Chain`: gauges `acceptcrypto.scanner.lag_blocks`, `head_block`,
`last_scanned_block`; counters `transfers`, `reorgs`, `failed_passes`. Health check `chain-scanner` (only when
enabled): Degraded when no pass succeeded for `StaleAfter` (2 min), with lag/failures in the data. Structured logs via
source-generated `LoggerMessage`.

**Chain ID, one setting.** `Chain:ChainId` post-configures `Persistence:ChainId` (removed from appsettings).

**Enabled flag.** `Chain:Enabled` (default false) registers the background services, validation and health check; the
Preview runs without a chain. `appsettings.Development.json` holds the local Anvil RPC URL, the mock token address and
`StartBlock: 0`; the compose stack (step 11) sets `Chain__Enabled=true`.

**Known limitation.** An unmatched payment recorded from a block that is later reorged away stays in the unmatched
list (it is informational; invoices are never affected). Roadmap: re-verify recent unmatched rows like unconfirmed
transfers.

## D15 — Merchant API, configuration, SSRF guard (step 4 of the plan: merchant API)

Reference: [API.md](API.md). Code: `src/AcceptCrypto.Service/Api`, `Configuration`, `Health`;
`src/AcceptCrypto.Infrastructure/Merchant` (merchant options + validator) and `Webhooks` (webhook options, SSRF guard).

**Routes.** Versioned prefix `/v1` (the scripted payer already calls `GET /v1/invoices/{id}` with `X-Api-Key`, D13).
Minimal APIs with typed results (`Results<…>`), so the OpenAPI document (`Microsoft.AspNetCore.OpenApi`, OpenAPI 3.1)
lists the real status codes. UI: Scalar at `/scalar` (lighter than Swagger UI, renders 3.1). Both are public — the
document contains no secrets and the API is useless without a key.

**Wire format.** snake_case JSON and enums (`paid_late`), amounts as decimal **strings** (a JSON number `10.5` is a
400: money never goes through binary floating point). The invoice response nests the FR-4 payer details under
`payment`, which is what the payer and the payment page read; `metadata` / `external_order_id` stay outside it (FR-29).

**Authentication.** A custom `AuthenticationHandler` for `X-Api-Key`: both values are SHA-256 hashed, then compared
with `CryptographicOperations.FixedTimeEquals` (no length/content timing leak). One merchant → one key, no roles.
The challenge writes Problem Details (`api_key_missing` / `api_key_invalid`). The key is never logged (test:
`Secrets_never_appear_in_logs`).

**Configuration (FR-0) is validated at startup** (`ValidateOnStart`): API key and webhook secrets ≥ 32 chars, previous
secret ≠ current, xpub parses (xprv refused), webhook URL passes the static SSRF check, invoice limits pass the
domain's `InvoicePolicy.EnsureValid`, retry schedule sane. Messages name the setting, never the value. Consequence:
the service no longer starts without merchant configuration — the Preview (`scripts/dev.sh`) therefore runs with
`ASPNETCORE_ENVIRONMENT=Development`, whose `appsettings.Development.json` holds clearly-labelled throwaway values.
Secret rotation (FR-0): `Merchant:WebhookSecret` + optional `Merchant:PreviousWebhookSecret`; step 7 decides how both
are used for signatures (e.g. one signature per secret in the header during rotation).

**Validation and errors (FR-20).** Field checks in the endpoint (presence, amount syntax, header syntax) plus the
domain's own rules: a `DomainException` from `Invoice.Create` is mapped by its code to a field
(`amount_out_of_range` → `amount`, …) and returned as `400 validation_failed` — the rules live once, in the domain.
Malformed JSON → `400 malformed_request` (`ThrowOnBadRequest` + an `IExceptionHandler`); unknown routes → `404` via
status-code pages; unexpected exceptions → `500` without internals. Every problem has `code` and `trace_id`; `type`
links to the error list in API.md.

**Idempotency (FR-2).** Optional `Idempotency-Key` header; fingerprint = SHA-256 of the canonical request (amount
value, order ID, metadata sorted, lifetime). Replay → `200` + `Idempotent-Replayed: true`; different request with
the same key → `422 idempotency_key_reused` (IETF idempotency-key draft semantics).

**Cancel.** `pending` → `cancelled` (D6) via the unit of work (event in the outbox atomically). Already cancelled →
`200` without a second event (idempotent). Anything else → `409 invoice_not_cancellable` with `current_status`
(`status` is reserved by RFC 9457).

**Pagination (FR-18).** Keyset on `(created_at, id)` descending (repository from step 3), unmatched by `id`; the
cursor is opaque base64url with a version prefix. `created_from` inclusive, `created_to` exclusive.

**Resend (FR-15).** `OutboxEvent.Resend` (status pending, attempts 0, due now) under the `xmin` row-version check,
retried 3× on a concurrency conflict, then `409`. Same event ID and payload bytes; history kept. Answer `202`.

**SSRF (NFR-6).** `WebhookUrlGuard` (Infrastructure, used by the step 7 dispatcher):
1. Startup (`CheckSyntax`, no DNS — the demo shop may not be up yet): absolute http(s), no credentials/fragment,
   https unless allowlisted, no blocked IP literal, no `localhost`/`*.localhost` unless allowlisted.
2. `CheckAsync`: resolves the host; **every** address must be public (one private answer refuses the URL).
3. `CreateHttpHandler()`: a `SocketsHttpHandler` whose `ConnectCallback` resolves again, checks, and connects to the
   address it just checked — DNS rebinding between check and request cannot reach the internal network. No redirects,
   no proxy. Blocked: 0/8, 10/8, 100.64/10, 127/8, 169.254/16 (cloud metadata), 172.16/12, 192.0.0/24, TEST-NETs,
   192.168/16, 198.18/15, multicast/reserved/broadcast, `::`, `::1`, fe80::/10, fec0::/10, fc00::/7, ff00::/8,
   2001:db8::/32, and IPv4-mapped/compatible/NAT64/6to4 forms of blocked IPv4.
   `Webhooks:AllowedHosts` (exact host match) bypasses the address check and allows plain http — for `demo-shop` on the
   Docker network and `localhost` in development.

**Health (NFR-7).** `/health` keeps its meaning (liveness + scanner state, no dependencies → green without a DB, as
the Preview needs); `/health/ready` adds `database` and `rpc` (chain ID must match) with a JSON report without
exception texts (they may contain host names).

**Logs (NFR-7).** Production console logs are JSON (`Logging:Console:FormatterName = json`, UTC timestamps, scopes —
the request's trace ID is in the scope); Development uses the simple formatter. Endpoint events use source-generated
`LoggerMessage` with IDs/amounts only, never metadata or keys. A startup line summarises the configuration (webhook
origin, number of secrets, chain) without values.

## D16 — Webhook delivery: claiming, signing, retries, rotation, payload (step 7)

Reference: [webhooks.md](webhooks.md). Code: `src/AcceptCrypto.Infrastructure/Webhooks`.

**Claiming (NFR-3).** One SQL statement: `FOR UPDATE SKIP LOCKED` picks due pending rows nobody else holds and moves
their `next_attempt_at` forward by `Webhooks:ClaimLease` (a lease) before returning them. The HTTP request then runs
*outside* any transaction (no long-held row locks, no open transaction while a merchant is slow); the result is
written with the `xmin` row version. A crashed dispatcher only delays its events by the lease → at-least-once,
deduplicated by event ID on the merchant side (FR-16). Several service instances can run dispatchers safely
(test: four parallel dispatchers deliver 24 events exactly once).

**Per-invoice order.** Only the oldest *pending* event of an invoice is claimable, so events of one invoice arrive in
causal order (`confirming` before `paid`), never in parallel. Trade-off: an event in retry holds back newer events of
the same invoice — acceptable because all events go to the same URL (if one fails, the next would too); a **failed**
event stops blocking. Events of different invoices are independent. Rejected: Stripe-style "no order guarantee" (more
work for the integrator, who is the target user).

**Signature (FR-13).** `v1=hex(HMAC-SHA256(secret, "{timestamp}.{body}"))` in `AcceptCrypto-Signature`, timestamp
(Unix seconds of the *attempt*) in `AcceptCrypto-Timestamp`, event ID in `AcceptCrypto-Event-Id`, plus
`AcceptCrypto-Event-Type`. Headers without the `X-` prefix (RFC 6648). Each attempt is re-signed, so the receiver's
5-minute tolerance applies to the delivery, not the event age. Reference vectors are in webhooks.md and the tests.

**Rotation (FR-0).** The service signs with the current (new) secret; while `Merchant:PreviousWebhookSecret` is set it
adds a second `v1=` entry with the old secret (the header is a comma-separated list). This makes rotation order-free:
the service can switch before or after the merchant's app. Verification with several secrets is the SDK's job (FR-26).

**Retries (FR-14).** `InitialRetryDelay·2^(n−1)` capped at `MaxRetryDelay`, ±20 % jitter (shared `Backoff` helper);
failed when `MaxAttempts` is reached or the next attempt would fall outside `RetryWindow`. The window is counted from
`retry_cycle_started_at` (new column, migration `WebhookRetryCycle`): creation, or the last manual resend — otherwise a
resend of a day-old event would fail after one attempt. Any non-2xx, timeout, connection error or SSRF refusal is a
failed attempt; no special case for 410 (single configured merchant; it can fix its endpoint and resend).

**Payload.** `{"id","type","created_at","api_version","data":{"invoice":{snapshot},"previous_status"|"transfer"}}`.
The snapshot replaced the flat fields of step 3 (no consumers yet). `api_version` = `2026-11-17`; additive changes keep
the version. No payment-page token, idempotency key or derivation index in webhooks.

**Metrics (NFR-7).** Meter `AcceptCrypto.Webhooks`: queue length (pending), due, failed, oldest pending age (gauges,
refreshed by the dispatcher), attempts by outcome and duration.

**Hosting.** `AddAcceptCryptoWebhooks` (merchant options + SSRF guard + named `HttpClient` with the guarded handler +
dispatcher); `Webhooks:DispatcherEnabled=false` turns the background service off (API tests). Without a database the
dispatcher logs and backs off (max 1 min); `/health` stays green, as the Preview needs.

## D17 — Payment page: technology, live status, QR, privacy (step 8)

**Technology: server-rendered HTML from a minimal-API endpoint + one small vanilla JS file (no Razor Pages, no
Blazor, no JS framework, no CDN).** The page is a single screen with ~10 dynamic values; Razor Pages/Blazor would add
MVC/Razor/circuit infrastructure to an API-only service for no gain, and a SPA framework would need a JS build. The HTML
is a C# raw-string template (`PaymentPageRenderer`, every value `HtmlEncoder`-encoded); `pay.css`/`pay.js` are embedded
resources served at `/pay/assets/*?v={sha256}`. Static facts (address, amount, network, token, deadline, QR) are
rendered on the server, so the page is useful without JS; the status block is filled by JS from the embedded initial
state and then from polling.

**Live status: polling, not SSE.** `GET /api/public/invoices/{token}` every 3 s (configurable). Reasons: status changes
come from background workers (scanner/expiry) in possibly other instances — SSE would need a change feed (LISTEN/NOTIFY
or per-connection DB polling anyway); polling works through every proxy, needs no long-lived connections, and the same
JSON serves the scripted payer. The client slows down for `expired` (late payment possible), stops on final statuses
(`paid`, `overpaid`, `paid_late`, `cancelled`), backs off exponentially on errors (max 30 s, "Reconnecting…"), stops on
404 and pauses in hidden tabs.

**Public view is an allowlist type.** `PublicInvoiceView` is the only thing the page, QR and JSON are built from; it has
no invoice ID, order ID, metadata, idempotency key or transfer senders. A test pins the exact JSON field set and checks
that page/JSON/QR never contain the order ID, metadata, invoice ID or payer address.

**QR.** Server-side SVG via QRCoder (MIT, no native deps). Default content is an EIP-681 ERC-20 transfer URI
(`ethereum:{token}@{chainId}/transfer?address={deposit}&uint256={baseUnits}`) for the **remaining** amount, so an
underpaid invoice's QR asks for the top-up; an "Address only" toggle shows a plain-address QR for wallets without
EIP-681 support. The QR is only shown while `accepting_payment` (pending/underpaid before the deadline); after the
deadline, when paid or cancelled the page says not to send funds.

**Token handling / hardening.** 256-bit token; malformed tokens are rejected without a DB query; unknown → 404 (Problem
Details for JSON, a friendly HTML page otherwise). `Cache-Control: no-store`, `Referrer-Policy: no-referrer`,
`X-Robots-Tag: noindex`, strict CSP without `unsafe-inline` (hence no inline script/style), `X-Frame-Options: DENY`,
per-IP fixed-window rate limit (120/min). Deposit addresses are shown EIP-55-checksummed (stored lowercase).

**Sample mode.** `PaymentPage:EnableSample` (Development only) serves `/pay/sample?status=…` with fabricated data and no
database, for design review in the Preview and README screenshots.

**Not done / roadmap:** wallet connection (D8), localisation, SSE/WebSocket push.

## D18 — SDK (`AcceptCrypto.NET` NuGet package, step 9)

**Package.** Project `src/AcceptCrypto.Sdk`, package ID **`AcceptCrypto.NET`** (D10), `net10.0` only (FR-22: no
`net8.0` target — the MVP does not deliberately target older apps). Only dependency: the `Microsoft.AspNetCore.App`
shared framework (DI, options, `IHttpClientFactory`, logging, routing) — no third-party packages. README, MIT license
expression, repository URL + commit, XML docs, `.snupkg` symbols; SourceLink comes from the .NET SDK
(`PublishRepositoryUrl`, `EmbedUntrackedSources`), verified: the PDB maps sources to `raw.githubusercontent.com/…/<commit>`.

**Registration (FR-23).** `services.AddAcceptCrypto()` binds section `AcceptCrypto` (`BindConfiguration`, so reloads
work); overloads take an `Action<AcceptCryptoOptions>` (applied after the section) or an `IConfiguration` section.
Returns the typed client's `IHttpClientBuilder` so the merchant can add a resilience handler. Options are validated
at startup (`ValidateOnStart`, messages without secret values); `BaseUrl`/`ApiKey` are checked when the client is
used and `WebhookSecret` when the endpoint is mapped, so a webhook-only or client-only app needs only its half.

**Money.** The SDK has its own `UsdcAmount` (BigInteger base units, JSON string, numbers refused) — it cannot reference
the Domain project (it ships alone). Unmatched payments can carry up to uint256 amounts, which `decimal` cannot hold;
`ToDecimal()` is offered for convenience.

**Client (FR-24/25).** `IAcceptCryptoClient`: create (idempotency key: caller's, else a generated GUID so HTTP-level
retries are safe; `CreatedInvoice.IdempotentReplayed` from `Idempotent-Replayed`), get (details), list + `ListAll…`
async enumerators following the cursor, cancel, unmatched list, resend. The API key is added per request from
`IOptionsMonitor` (key rotation without restart). Errors: Problem Details → `AcceptCryptoApiException` subclasses by
status (400 validation, 401 authentication, 404 not found, 409 conflict with `CurrentStatus`, 422 idempotency key
reused with `InvoiceId`), anything else → `AcceptCryptoApiException` with `StatusCode`/`ErrorCode`/`TraceId`/
`Extensions`; network errors and timeouts → `AcceptCryptoConnectionException`; caller cancellation stays
`OperationCanceledException`; an unexpected payload → `AcceptCryptoException`. Enums deserialize tolerantly: unknown
values (a newer service) become `Unknown` instead of breaking the merchant's app.

**Webhooks (FR-26/27, FR-16).** `AcceptCryptoWebhookVerifier` (usable without ASP.NET Core) implements
docs/webhooks.md §2: missing headers / non-numeric timestamp / `|now − ts| > tolerance` (default 5 min, max 1 h) /
no matching `v1=` entry → rejected; every configured secret is compared with every `v1=` entry using
`CryptographicOperations.FixedTimeEquals` without early exit; unknown schemes ignored; at most 8 entries examined.
Secrets: `WebhookSecret` + `AdditionalWebhookSecrets` (rotation, read per request). Typed events:
`InvoiceStatusChangedEvent` (Status/PreviousStatus), `PaymentRevertedEvent` (Transfer), `UnknownAcceptCryptoEvent`
(newer types, raw `data`); every event carries the invoice snapshot and the raw JSON. A body without
`id`/`type`/`api_version`/`data.invoice` is malformed (400).
`MapAcceptCryptoWebhook(pattern, handler)` (handler `(AcceptCryptoEvent)`, `(AcceptCryptoEvent, HttpContext)` or a DI
class `IAcceptCryptoWebhookHandler`): raw body with size limit (413) → verify (400 Problem Details with a short
reason, logged as a warning without secrets) → `IWebhookEventDeduplicator.TryBeginAsync` (already processed → 200
without calling the handler; in progress → 409 so the service retries) → handler → `CompleteAsync` and 200, or on
exception `AbandonAsync`, error log and 500 (retry). Default deduplicator: in memory (7 days, 100 000 IDs, stale
claims expire after 5 min); a multi-instance merchant registers a DB-backed one.

**Contract safety.** `IntegrationTests/Api/SdkContractTests.cs` runs the SDK client against the real service (real
PostgreSQL) and verifies webhooks produced by the real dispatcher during a rotation, so the two sides cannot drift
apart unnoticed.
