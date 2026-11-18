# Progress — AcceptCrypto.NET

Spec: `AcceptCrypto.NET_SPEC.md` (project file, not in the repo). Decisions: [DECISIONS.md](DECISIONS.md).

## Plan

- [x] **1. Solution skeleton, decision log, CI and progress file** (this file, DECISIONS.md, CI workflow in `docs/ci/ci.yml`, /health stub)
- [x] **2. Domain: money (USDC base units), invoice aggregate, status machine incl. `cancelled`, unit tests** (see step notes, DECISIONS D11)
- [x] **3. Persistence: PostgreSQL / EF Core, migrations, outbox, idempotency keys, optimistic concurrency** (see step notes, DECISIONS D12)
- [x] **4. Merchant API: create/get/list/cancel invoices, unmatched payments, resend event; API key auth, Problem Details, OpenAPI, config validation, SSRF guard, readiness checks** (see step notes, DECISIONS D15, API.md)
- [x] **Local chain: Anvil, mock USDC, xpub derivation (former 5), scripted payer, chain test helpers** (see step notes, DECISIONS D13)
- [x] **6. Chain scanner: eth_getLogs, checkpoint, chunking, confirmations, reorg handling, unmatched, expiry** (see step notes, DECISIONS D14)
- [x] **7. Webhooks: outbox dispatcher (SKIP LOCKED + lease, per-invoice order), HMAC-SHA256 signing, rotation, backoff + jitter, failed state, resend, metrics** (see step notes, DECISIONS D16, [webhooks.md](webhooks.md))
- [ ] 8. Payment page: instructions + QR + live status
- [ ] 9. SDK: DI registration, typed client, webhook verification, typed events, README, tests
- [ ] 10. Demo shop (scripted payer and mock USDC are done in the local-chain step)
- [ ] 11. One-command stack (docker compose), integration tests (Testcontainers, forced reorg), S1–S7
- [ ] 12. README packaging (GIF, architecture diagram, "hard parts" section), testnet guide, PR

(Steps after 1 are a working outline; refine as the project plan evolves.)

## How to build / run / test

Requires the .NET 10 SDK (`global.json`: 10.0.x).

```bash
dotnet build AcceptCrypto.NET.slnx
dotnet test AcceptCrypto.NET.slnx
dotnet run --project src/AcceptCrypto.Service      # Development: /health, /scalar, /v1/* (needs merchant config, see API.md)
dotnet pack src/AcceptCrypto.Sdk -c Release -o artifacts   # AcceptCrypto.NET.<ver>.nupkg
scripts/local-chain.sh                              # Anvil + mock USDC + funded payer (needs Foundry)
dotnet run --project src/AcceptCrypto.Payer -- --help   # scripted payer
```

Tree Work Preview runs `npm install && npm run dev`: `package.json` → `scripts/dev.sh`, which installs the
.NET 10 SDK into `~/.dotnet` if missing and runs the service on `0.0.0.0:$PORT`.

CI: workflow file is stored at `docs/ci/ci.yml` (see open points) — restore, build (Release), test (trx + coverage), pack SDK, on every push/PR.
Runs on `ubuntu-latest` (Docker available for Testcontainers).

## Step notes

### Step 1 — skeleton (done)

- Created `AcceptCrypto.NET.slnx` with 6 src projects and 3 test projects (see DECISIONS.md D10).
- Project references: Infrastructure → Domain; Service → Domain, Infrastructure; DemoShop → Sdk;
  Domain.Tests → Domain; Sdk.Tests → Sdk; IntegrationTests → Service.
- Stubs: `InvoiceStatus` enum (+ `IsTerminal`) in Domain, `AcceptCryptoOptions` in SDK,
  `/health` (ASP.NET Core health checks) in Service and DemoShop, `pay` console prints usage.
- Tests: domain status terminality, SDK options default, `/health` via `WebApplicationFactory<Program>`.
- Analyzer notes: test projects suppress CS1591, CA1707, CA2007, CA1515 (see `.editorconfig`);
  `GenerateDocumentationFile` stays on everywhere so IDE0005 (unused usings) is enforced on build.


### Step 2 — domain (done)

All in `src/AcceptCrypto.Domain` (no package dependencies), decisions in DECISIONS.md **D11**.

- `Money/UsdcAmount` — base units in `BigInteger`, strict `Parse`/`TryParse`, `ToString()` (canonical) /
  `ToFixedString()`, `+ - < > <= >=`, `SubtractOrZero`.
- `Chain/ChainTransfer` (validated, lower-cased observation of an ERC-20 `Transfer`), `TransferId` (tx hash + log index),
  `EvmHex` (address/hash validation).
- `Invoices/Invoice` aggregate: `Create(NewInvoice, InvoicePolicy, TimeProvider)`, `RecordTransfer` →
  `TransferRecordResult` (`Accepted` / `Duplicate` / `InvoiceClosed` → caller stores unmatched), `ConfirmTransfer`,
  `RevertTransfer`, `CheckDeadline`, `Cancel`. Each returns the domain events in causal order.
  Totals: `ConfirmedTotal`, `ConfirmedOnTimeTotal`, `UnconfirmedTotal`, `RemainingAmount`, `ExcessAmount`.
  `Invoice.IsTransitionAllowed` exposes the edge table (diagram + `pending → cancelled` + `expired → confirming`).
- `ObservedTransfer` (state Seen/Confirmed/Reverted, `IsOnTime`), `InvoiceStatusChanged` / `PaymentReverted` events
  (`InvoiceEventTypes`), `InvoicePolicy` (min/max amount, default/min/max lifetime, metadata/ID length limits;
  `EnsureValid()` for startup), `PaymentPageToken`, `DomainException` + `DomainErrorCodes` (for Problem Details).
- Persistence prep: private parameterless constructors and private setters, transfers in a `_transfers` backing field.
- Tests (`tests/AcceptCrypto.Domain.Tests`, 135): money parsing/formatting/comparison, chain data validation,
  invoice creation & deadlines (`FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`), every
  diagram edge, edge cases of D11, idempotency, events, and a test that the transition table equals the diagram.

Notes for next steps:
- Step 3 (storage): map `UsdcAmount` as `numeric(78,0)` of base units (value converter on `BaseUnits`);
  unique `(tx_hash, log_index)` per chain; keep outbox order = event list order; row version on invoices (NFR-3).
- Step 6 (scanner): call `RecordTransfer` then `ConfirmTransfer` in the same transaction when a transfer is
  already deep enough; run `CheckDeadline` only once the scanner has caught up past the deadline.

### Step 3 — persistence (done)

All in `src/AcceptCrypto.Infrastructure/Persistence`, decisions in DECISIONS.md **D12**.

- `AcceptCryptoDbContext` (EF Core 10 + Npgsql, snake_case): invoices (+ owned `observed_transfers`), unmatched
  payments, outbox events + delivery attempts, scanner checkpoints, idempotency keys, derivation-index sequence.
  Amounts `numeric(78,0)` base units; timestamps `timestamptz` (normalised to UTC); metadata `jsonb`.
- Migration `InitialSchema` (generated; analyzers disabled for `Persistence/Migrations` in `.editorconfig`).
- Repositories (singletons over `IDbContextFactory`): `IInvoiceRepository` (`CreateAsync` idempotent create,
  `GetAsync`, `GetByPublicTokenAsync`, `FindByDepositAddressAsync`, `ListDueForExpiryAsync`, keyset `ListAsync`,
  **`UpdateAsync` = unit of work: domain op + outbox in one transaction, optimistic concurrency retry**),
  `IUnmatchedPaymentRepository` (idempotent insert, list), `IScannerCheckpointRepository` (upsert),
  `IOutboxRepository` (due list, per-invoice history, xmin-guarded update). Entities `OutboxEvent`
  (`RecordSuccess/RecordFailure/Resend`), `WebhookDeliveryAttempt`, `UnmatchedPayment`, `ScannerCheckpoint`.
  `OutboxEventFactory` builds the webhook JSON payload.
- Domain change: `ObservedTransfer` stores `TxHash` + `LogIndex` (persisted columns); `Id` is now computed from them.
- Service: `AddAcceptCryptoPersistence(configuration)` in `Program.cs` (connection string
  `ConnectionStrings:AcceptCrypto`, section `Persistence`: `ChainId`, `MaxConcurrencyRetries`, `MigrateOnStartup`).
  Nothing connects until first use, so `/health` and the Preview work without a database.
  `appsettings.Development.json` has a local-dev connection string (postgres/postgres on localhost, throwaway).
- Integration tests (`tests/AcceptCrypto.IntegrationTests/Persistence`, 23 tests; 24 with `/health`): idempotent create (replay,
  different body, 16 parallel requests → 1 invoice, rejected request does not burn the key), duplicate transfer
  (re-record, 8 parallel records → 1 row, DB unique constraint, unmatched dedupe incl. uint256 max amount),
  concurrency (stale writer conflicts even when only transfers changed; parallel confirmations → exactly one
  `invoice.paid`; cancel vs. payment race), outbox atomicity (causal order + payload, injected failure on the outbox
  insert rolls back the status change, domain error saves nothing, delivery history + resend keeps event ID,
  xmin conflict), list pagination/filters, expiry query, checkpoint upsert, migrations fully applied.
  Verified that disabling the aggregate version bump makes the concurrency tests fail.

How to run the integration tests:

```bash
dotnet test AcceptCrypto.NET.slnx          # Testcontainers starts postgres:17-alpine (needs Docker, as in CI)
# without Docker: point at any PostgreSQL server where the user may CREATE DATABASE
ACCEPTCRYPTO_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet test AcceptCrypto.NET.slnx
```

Each test creates and migrates its own database (`test_<guid>`) and drops it afterwards.
The agent container has no Docker, so this step was verified with a local PostgreSQL 15 through
`ACCEPTCRYPTO_TEST_POSTGRES`; the Testcontainers path runs in CI.

Migrations:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/AcceptCrypto.Infrastructure --output-dir Persistence/Migrations
ACCEPTCRYPTO_DESIGN_CONNECTION="Host=localhost;Database=acceptcrypto;Username=postgres;Password=postgres" \
  dotnet ef database update --project src/AcceptCrypto.Infrastructure
# or run the service with Persistence__MigrateOnStartup=true
```

### Step 4 — local chain: Anvil, mock USDC, xpub derivation, scripted payer (done)

Decisions in DECISIONS.md **D13**. (The plan's step 4 "Merchant API" is still open; this step took over the former
step 5 and the payer/mock-USDC part of step 10.)

- **Mock USDC**: `contracts/src/MockUsdc.sol` (minimal ERC-20, 6 decimals, open `mint`), compiled artifact committed
  at `src/AcceptCrypto.LocalChain/Contracts/MockUsdc.json` (embedded resource). Regenerate with
  `scripts/build-contracts.sh` (Foundry + jq) — only when the `.sol` changes.
- **`src/AcceptCrypto.LocalChain`** (TEST/DEV only, never referenced by the service): `AnvilAccounts` (well-known keys,
  fixed token address `0x5FbDB2315678afecb367f032d93F642f64180aa3`), `MockUsdc` (deploy / `EnsureDeployedAsync` /
  mint / transfer / balance, Nethereum typed messages, `TransferEventDto`), `AnvilClient` (block number/info, receipt,
  `MineAsync`, `SetAutomineAsync`, `SnapshotAsync`/`RevertAsync`, **`ReorgAsync(depth)`** via `anvil_reorg`,
  `ReorgToSnapshotAsync`, **`WarpAsync`** (time warp) and `SetNextBlockTimestampAsync`), `PaymentPlan.Split`.
- **Derivation** (Infrastructure, `Chain/XpubDepositAddressDeriver.cs`): `IDepositAddressDeriver.DeriveAddress(index)`
  → EIP-55 address of `xpub/index`; `AddAcceptCryptoDepositAddresses(configuration)` (section `Merchant:Xpub`) is
  registered in the service. Test xpub (public "abandon … about" mnemonic, TEST ONLY) in `appsettings.Development.json`.
- **Scripted payer** `src/AcceptCrypto.Payer` (assembly `pay`):

  ```bash
  pay setup                                  # deploy mock USDC (if missing) + mint 1,000,000 USDC to the payer
  pay <invoice> [--amount X] [--split N]     # <invoice> = http://host/pay/<token> | <token> | <invoice GUID> (+ --api-key)
  pay --to <address> --amount X [--split N]  # any address, e.g. an unmatched transfer (S7)
  pay balance [address] | pay mine [n] | pay reorg <depth> | pay warp <seconds>
  # options/env: --rpc ACCEPTCRYPTO_RPC_URL, --token ACCEPTCRYPTO_TOKEN_ADDRESS, --service ACCEPTCRYPTO_SERVICE_URL,
  #              --api-key ACCEPTCRYPTO_API_KEY, --private-key PAYER_PRIVATE_KEY
  ```
  Run with `dotnet run --project src/AcceptCrypto.Payer -- <args>`. Underpay/overpay = `--amount`, partial = `--split`
  (parts add up exactly). The payer checks the invoice's `chain_id` against the RPC and never uses the well-known key
  off chain 31337. Expected service endpoints/JSON: see D13 ("Payer ↔ service contract").
- **Tests**:
  - `tests/AcceptCrypto.Infrastructure.Tests` (new, unit): derivation vs. reference addresses from Foundry's `cast`
    (merchant test xpub, Anvil accounts), public vs. private-key derivation cross-check, determinism, tpub,
    index range, xprv rejection, bad input; payer logic (split, command line, invoice reference, payment-details JSON,
    embedded artifact). 52 tests.
  - `tests/AcceptCrypto.IntegrationTests/Chain` (real Anvil, collection `anvil`, chain reset per test):
    deploy/decimals/funding, Transfer log found by `eth_getLogs` filtered on `to`, **forced reorg** (`anvil_reorg`) and
    snapshot reorg remove the transfer and change the block hash, manual mining & confirmations, time warp; scripted
    payer end to end against a stub of the public payment-details endpoint (exact / split / under / over / `--to` /
    wrong chain / 404 / mine-reorg-warp commands). 14 tests.
  - `AnvilFixture` picks Anvil from `ACCEPTCRYPTO_TEST_ANVIL_RPC` (existing node — it is reset!), else an `anvil`
    binary (PATH, `~/.foundry/bin`, `ACCEPTCRYPTO_TEST_ANVIL_BIN`) on a free port, else Testcontainers
    `ghcr.io/foundry-rs/foundry:latest`. Verified here with Foundry 1.9.0 (anvil binary); the container path is
    untested (no Docker in the agent container). `docs/ci/ci.yml` now installs Foundry (`foundry-rs/foundry-toolchain@v1`).
  - Gotcha: after `anvil_reset` create a new Nethereum `Web3`/`Account` — a reused one can send with a stale nonce and
    hang waiting for a receipt (the fixture does this).

Verified: `dotnet test AcceptCrypto.NET.slnx` → 135 domain + 52 infrastructure + 1 SDK + 38 integration tests green
(PostgreSQL via `ACCEPTCRYPTO_TEST_POSTGRES`, Anvil binary); `scripts/local-chain.sh` deploys the token and funds the payer;
`pay --to … --split 3`, `mine`, `reorg`, `warp` checked by hand.

Notes for next steps:
- Merchant API / payment page: implement `GET /api/public/invoices/{token}` (and `GET /v1/invoices/{id}` with
  `X-Api-Key`) with the fields in D13, or adjust the payer. Allocate the invoice address with
  `IDepositAddressDeriver.DeriveAddress(nextval(invoice_derivation_index_seq))`.
- Scanner: token address for local = `AnvilAccounts.MockUsdcAddress`; use `AnvilClient.ReorgAsync` + `MineAsync` in
  the FR-10 test and `WarpAsync`/`SetNextBlockTimestampAsync` for late payments (block timestamp rule).
- Compose (step 11): Anvil container (`ghcr.io/foundry-rs/foundry`, `anvil --host 0.0.0.0`) + a one-shot
  `pay setup` container (or the service's startup) before the demo; service config `Merchant__Xpub` = the test xpub.

### Step 6 — chain scanner: detection, confirmations, reorgs, unmatched, expiry (done)

All in `src/AcceptCrypto.Infrastructure/Chain/Scanning`, decisions in DECISIONS.md **D14**.

- `JsonRpcChainClient` (`IChainRpcClient`, named `HttpClient`): `eth_chainId`, `eth_blockNumber`,
  `eth_getBlockByNumber`, `eth_getLogs` for `Transfer` with a recipient-topic filter. Errors → `ChainRpcException`
  (`RpcErrorCode` for JSON-RPC errors), never containing the RPC URL.
- `ChainScanner.RunPassAsync()` — one pass: chain-ID check, checkpoint hash check (reorg → rollback by
  `ReorgRollbackDepth`), fresh block-hash check of every unconfirmed transfer (revert → `payment.reverted`, rescan
  below it; N deep → confirm), chunked scan `[checkpoint+1, head]` (`MaxBlockRange`, adaptive halving on RPC range
  errors), record (+ confirm in the same transaction if already N deep), unmatched for closed invoices, checkpoint
  after every chunk. Returns `ScanPassResult` (counts, reorg flag).
- `InvoiceExpiryProcessor.RunOnceAsync()` — expires pending/underpaid invoices whose deadline is ≤ min(now,
  `ChainScanState.ScannedThrough`).
- `ChainScannerService` / `InvoiceExpiryService` (`BackgroundService`): loop with poll interval, exponential backoff
  with jitter on any failure, never crash the host. `ChainScanState`: shared progress + meter `AcceptCrypto.Chain`
  (lag/head/last-scanned gauges, transfers/reorgs/failed-pass counters). `ChainScannerHealthCheck` (`chain-scanner`).
- `AddAcceptCryptoChainScanner(configuration)` (called in `Program.cs`): section `Chain`; hosted services, options
  validation and health check only when `Chain:Enabled=true` (default false → Preview unaffected).
  `Chain:ChainId` now feeds `Persistence:ChainId`. `appsettings.Development.json`: local Anvil URL, mock token, `StartBlock: 0`.
- Repository additions: `IInvoiceRepository.ListDepositAddressesAsync`, `ListUnconfirmedTransfersAsync` (+ record
  `UnconfirmedTransfer`). Infrastructure now has a `FrameworkReference` to `Microsoft.AspNetCore.App` (hosting,
  `IHttpClientFactory`, health checks, metrics); the redundant `Options.ConfigurationExtensions` package was dropped.
- Tests:
  - `tests/AcceptCrypto.IntegrationTests/Scanner` (collection `anvil`, which now also has a `PostgresFixture`;
    `ScannerHarness` = fresh DB + reset Anvil + DI like the service, `FakeTimeProvider` starting at the chain's latest
    block time, `RestartAsync()` = new container on the same DB): exact → confirming → paid at N=3; underpaid + top-up
    → paid; overpaid (excess visible); split payment; expiry waits for the scanner, late payment → `paid_late`;
    on-time payment scanned after the deadline → `paid`; restart mid-payment with checkpoint reset → counted once;
    payment during downtime found after restart with `MaxBlockRange=2`; **forced reorg** (`anvil_reorg`) of a confirming
    payment → `payment.reverted`, `pending`, never `paid`; reorg of the top-up of a split payment → back to
    `underpaid`; transfer to a paid invoice → one unmatched row (also after a rescan); non-deposit addresses ignored.
    Hosting: the real `Program` host detects and confirms a payment in the background and `/health` is Healthy;
    unreachable RPC → retried with backoff, host keeps running, `/health` Degraded; backoff bounds. 16 tests.
  - `tests/AcceptCrypto.Infrastructure.Tests/Chain/JsonRpcChainClientTests` — filter/request shape, log parsing,
    JSON-RPC error code, HTTP error without URL, block parsing. 4 tests.

Verified: `dotnet test AcceptCrypto.NET.slnx` → 135 domain + 56 infrastructure + 1 SDK + 53 integration tests green
(PostgreSQL via `ACCEPTCRYPTO_TEST_POSTGRES`, Anvil binary 1.x). A mutation check (disabling the block-hash revert)
makes both reorg tests fail. Preview (`scripts/dev.sh`, Production, no chain/DB) → `/health` Healthy.

Run the scanner locally: `scripts/local-chain.sh` (Anvil + token), PostgreSQL on localhost, then
`ASPNETCORE_ENVIRONMENT=Development Chain__Enabled=true Persistence__MigrateOnStartup=true dotnet run --project src/AcceptCrypto.Service`.

Notes for next steps:
- API (step 4): list unmatched payments via `IUnmatchedPaymentRepository.ListAsync`; invoice details can show
  transfers with state/block/confirmations (head from `ChainScanState.Head`).
- Webhooks (step 7): events from the scanner are in `outbox_events` in causal order (`invoice.confirming`,
  `invoice.paid`, `payment.reverted`, `invoice.pending`, `invoice.expired`, `invoice.paid_late`, …).
- Compose (step 11): `Chain__Enabled=true`, `Chain__RpcUrl=http://anvil:8545`, `Chain__TokenAddress=0x5FbD…0aa3`,
  `Chain__StartBlock=0`; consider `anvil --block-time 1` so idle chains still advance block time. Testnet:
  `Chain__ChainId=84532`, Circle USDC address, `Confirmations` ≥ 3, `StartBlock` = a recent block, RPC URL as a secret.

### Step 4 — merchant REST API (done)

Reference: [API.md](API.md); decisions in DECISIONS.md **D15**.

- Endpoints (`src/AcceptCrypto.Service/Api/MerchantApi.cs`, all under `/v1`, API key in `X-Api-Key`):
  `POST /v1/invoices` (Idempotency-Key, 201/200-replay/422-reuse, payment details + `payment_page_url`),
  `GET /v1/invoices/{id}` (transfers with confirmations + webhook events with delivery attempts),
  `GET /v1/invoices` (status / created_from / created_to, keyset cursor, limit), `POST /v1/invoices/{id}/cancel`
  (pending only, 409 otherwise, idempotent), `GET /v1/unmatched-payments` (cursor), `POST /v1/events/{id}/resend`
  (202, same event ID/payload).
- Configuration (FR-0), validated at startup without echoing values: `Merchant` (ApiKey, WebhookUrl, WebhookSecret,
  PreviousWebhookSecret, Xpub — `MerchantOptions` + `MerchantOptionsValidator` in Infrastructure), `Invoices`
  (min/max amount, default/min/max lifetime → `InvoicePolicy`), `Webhooks` (AllowedHosts, retry schedule, timeout),
  `Api` (PublicBaseUrl, page sizes), `Chain` gained `NetworkName` and `TokenSymbol`. Service has a `UserSecretsId`.
- Auth: `ApiKeyAuthenticationHandler` (constant-time compare, Problem Details challenge). Errors: Problem Details
  everywhere (`code` + `trace_id`), `ApiExceptionHandler`, status-code pages. OpenAPI 3.1 at `/openapi/v1.json` with
  the API key scheme, Scalar UI at `/scalar`.
- SSRF: `WebhookUrlGuard` (Infrastructure/Webhooks): static check at startup, DNS check, and a guarded
  `SocketsHttpHandler` (connect-time check, no redirects/proxy) for the step 7 dispatcher.
- Health: `/health` unchanged (liveness + scanner), new `/health/ready` with `database` and `rpc` checks (JSON).
  Logs: JSON console in Production, simple in Development; `LoggerMessage` events for create/replay/cancel/resend;
  redacted startup configuration summary.
- `IOutboxRepository.UpdateAsync` now loads the delivery attempts (the resend response shows the history).
- Preview: `scripts/dev.sh` defaults `ASPNETCORE_ENVIRONMENT=Development` (throwaway merchant values in
  `appsettings.Development.json`), because the service now refuses to start without merchant configuration.
- Tests:
  - `tests/AcceptCrypto.IntegrationTests/Api` — `ApiHarness` (real `Program` host via `WebApplicationFactory`, fresh
    migrated PostgreSQL database per test, `FakeTimeProvider`, log capture). `MerchantApiTests` (46 cases): auth on
    all 6 endpoints (missing / wrong key), health + OpenAPI + Scalar public, create (details, address = derivation,
    link, no metadata in `payment`), idempotency (replay incl. equivalent body, reuse → 422, 10 parallel → 1 invoice,
    failed request does not burn the key), 13 validation cases + 4 malformed bodies, details with transfers/events,
    404s, pagination over 3 pages, status/date filters, invalid query params, cancel (once, 409 when confirming, 404),
    unmatched list with cursor, resend keeps ID/payload/history, secrets absent from logs, the scripted payer's
    `PaymentDetailsClient` reads `GET /v1/invoices/{id}`. `ConfigurationValidationTests` (24): every invalid setting
    stops startup without leaking the value (incl. SSRF: 127.0.0.1, 169.254.169.254, 10/8, ::1, localhost, http,
    credentials), allowlisted internal hosts accepted. `ServiceFactory` (scanner hosting tests) now adds the test
    merchant settings.
  - `tests/AcceptCrypto.Infrastructure.Tests/Webhooks/WebhookUrlGuardTests` (41): address classification (IPv4/IPv6
    incl. mapped/NAT64/6to4), DNS answers (public / private / mixed / unresolvable / localhost), allowlist, and a real
    local TCP server proving the guarded handler refuses loopback (also after DNS rebinding) and connects when
    allowlisted.

Verified: `dotnet test AcceptCrypto.NET.slnx` → 135 domain + 97 infrastructure + 1 SDK + 123 integration tests green
(PostgreSQL via `ACCEPTCRYPTO_TEST_POSTGRES`, Anvil binary). Service in Development: `/health` Healthy, `/scalar`
renders the API reference, a create → list → get round trip works against local PostgreSQL.

Try it locally (PostgreSQL on localhost with postgres/postgres):

```bash
ASPNETCORE_ENVIRONMENT=Development Persistence__MigrateOnStartup=true dotnet run --project src/AcceptCrypto.Service
curl -s -X POST http://localhost:5052/v1/invoices -H 'X-Api-Key: dev_api_key_00000000000000000000000000000' \
  -H 'Idempotency-Key: demo-1' -H 'Content-Type: application/json' -d '{"amount":"25.50","external_order_id":"order-1"}'
# open http://localhost:5052/scalar
```

Notes for next steps:
- Webhooks (step 7): build the delivery `HttpClient` on `WebhookUrlGuard.CreateHttpHandler()` (and/or call
  `CheckAsync` before each attempt; a refusal is a failed attempt with a clear `last_error`). Retry schedule:
  `WebhookOptions` (`InitialRetryDelay` doubling to `MaxRetryDelay`, `MaxAttempts`, `RetryWindow`, `RequestTimeout`).
  Secrets: `MerchantOptions.WebhookSecrets` (current first, previous during rotation).
- Payment page (step 8): `payment_page_url` already points to `/pay/{token}`; the payer expects
  `GET /api/public/invoices/{token}` returning the same `payment` object (no metadata / order ID).
- SDK (step 9): the contracts are in `Api/Contracts.cs` and API.md; error `code`s are stable for typed exceptions.
- Compose (step 11): set `Merchant__ApiKey`, `Merchant__WebhookSecret`, `Merchant__WebhookUrl=http://demo-shop:8080/…`,
  `Merchant__Xpub`, `Webhooks__AllowedHosts__0=demo-shop`, `Api__PublicBaseUrl=http://localhost:<port>`; readiness
  probe `/health/ready`.

### Step 7 — webhook delivery: signature, retries, rotation (done)

Contract for receivers (and the SDK, step 9): **[webhooks.md](webhooks.md)** — headers, signature algorithm with
reference vectors, payload schema, delivery semantics, rotation procedure, configuration, metrics. Decisions: **D16**.

- `Infrastructure/Webhooks`: `WebhookSignature` (compute / header / verify), `WebhookRetryPolicy` (backoff + jitter,
  attempt limit, window), `WebhookDispatcher` (claim → SSRF check → signed POST → record attempt),
  `WebhookDispatcherService` (BackgroundService, never crashes on DB/merchant errors), `WebhookMetrics`
  (meter `AcceptCrypto.Webhooks`), `AddAcceptCryptoWebhooks` (replaces `AddAcceptCryptoMerchant` in `Program.cs`).
- Persistence: `IOutboxRepository.ClaimDueAsync` (single SQL: `FOR UPDATE SKIP LOCKED` + lease, oldest pending event
  per invoice only) and `GetQueueStatsAsync`; `OutboxEvent.RetryCycleStartedAt` (migration `WebhookRetryCycle`),
  reset by `Resend`; `RecordFailure` no longer downgrades an event delivered meanwhile.
- Payload now `{"id","type","created_at","api_version","data":{"invoice":{snapshot},…}}` (`OutboxEventFactory`).
- Headers: `AcceptCrypto-Event-Id`, `AcceptCrypto-Event-Type`, `AcceptCrypto-Timestamp`, `AcceptCrypto-Signature: v1=…[,v1=…]`.
- Rotation: service signs with `Merchant:WebhookSecret`; with `Merchant:PreviousWebhookSecret` set it adds a second
  `v1=` entry. The SDK must accept any configured secret against any `v1=` entry, with a 5-min timestamp tolerance.
- Config: new `Webhooks:DispatcherEnabled/PollInterval/BatchSize/MaxParallelDeliveries/ClaimLease/DispatcherErrorDelay`;
  the short demo retry profile is in `appsettings.Development.json` (5 s → 1 min, 10 min window).
- Tests: unit (`Infrastructure.Tests/Webhooks`): signature reference vectors (computed with Python `hmac`, checked
  with `openssl`), rotation header, tampering; retry schedule (default ≈ 24 h, jitter bounds, limit, window, resend
  = new window), option validation. Integration (`IntegrationTests/Webhooks`, real PostgreSQL + Kestrel receiver on
  127.0.0.1): signed delivery of the stored bytes, rotation signatures, **S5** (receiver down → backoff 30 s/60 s →
  receiver back → delivered once), non-2xx → failed after the limit → resend delivers with the **same event ID and
  body**, window exhaustion, timeout, SSRF refusal via DNS answer, 4 parallel dispatchers → each event exactly once,
  lease expiry, per-invoice order + head-of-line, failed event does not block, queue metrics, hosted service survives
  the outage; payload schema (exact keys, no token/idempotency key). `ApiHarness` disables the dispatcher.
- Verified: whole solution 135 + 110 + 1 + 137 tests green against local PostgreSQL 15
  (`ACCEPTCRYPTO_TEST_POSTGRES`); the service starts in Development without a database, `/health` 200, the
  dispatcher logs and backs off.

Next steps rely on: SDK (step 9) implements verification per webhooks.md §2 (tests can reuse the vectors) and typed
events per §3; demo shop (step 10) receives at `/webhooks/acceptcrypto`; compose (step 11) sets
`Webhooks__AllowedHosts__0=demo-shop` and the demo retry profile.

## Open points

- **CI activation (manual, owner):** the platform's GitHub token lacks the `workflow` scope, so the workflow cannot be
  pushed to `.github/workflows/`. The owner copies `docs/ci/ci.yml` to `.github/workflows/ci.yml` on GitHub.
  Agents must not commit files under `.github/workflows/` (the push is rejected). The README badge points to `ci.yml`.

- CI badge added to README (repo: github.com/alena-dev-soft/accept-crypto-net).
- Default N confirmations (D4): 2 (local Anvil), recommend 3+ on Base Sepolia — see D14. Open: unmatched rows from reorged blocks are not removed (D14 known limitation).
- Payment page UI technology (D8) is chosen in step 8.
- Step 3 follow-ups: step 4 maps `KeyReusedWithDifferentRequest` to a Problem Details error and computes the request
  fingerprint (e.g. SHA-256 of the canonical create request) (step 6 done: `Chain:ChainId` feeds
  `Persistence:ChainId`, `InvoiceClosed` transfers are recorded as unmatched); step 7 done (claiming with `FOR UPDATE SKIP LOCKED`, per-invoice order, D16); step 11 compose sets
  `ConnectionStrings__AcceptCrypto` and `Persistence__MigrateOnStartup=true`. DB/RPC readiness checks exist since step 4 (`/health/ready`).
