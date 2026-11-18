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
- [x] **8. Payment page: instructions + QR (EIP-681) + live status (polling), public status JSON** (see step notes, DECISIONS D17, API.md §Payment page)
- [x] **9. SDK (`AcceptCrypto.NET` package): DI registration, typed client, Problem Details → typed exceptions, webhook verification + rotation + tolerance, typed events, dedup hook, `MapAcceptCryptoWebhook`, README, tests** (see step notes, DECISIONS D18, [SDK README](../src/AcceptCrypto.Sdk/README.md))
- [x] **10. Demo shop on the SDK + one-command stack (docker compose), demo scripts** (see step notes, DECISIONS D19, [demo.md](demo.md))
- [x] **11. End-to-end tests S1–S7 (+ FR-8/9, FR-10, FR-26), observability (`/metrics`, webhook health check), CI test report** (see step notes, DECISIONS D20, [TESTS.md](TESTS.md), [observability.md](observability.md))
- [x] **12. README and architecture documentation: value line + badge, payment-cycle GIF, architecture + state diagrams (Mermaid), Docker-only quick start, S1–S7 walk-through, "how the hard parts are solved" with proving tests, SDK, decisions, roadmap, no-mainnet warning** (see step notes)
- [x] **13. Base Sepolia profile + guide, packaging drafts: `appsettings.BaseSepolia.json`, `docker-compose.base-sepolia.yml`, `base-sepolia.env.example`, `pay xpub [new]`, [testnet.md](testnet.md) (incl. video script), [marketing/](marketing/) drafts (Upwork card, dev.to article, Project Catalog offer)** (see step notes, DECISIONS D21)
- [x] **14. Definition of done checked on a clean clone (see step notes: table ✅/❌), fixes: microsecond clock (FR-2 replay byte-identical), Development settings = compose stack, payer `--then-reorg`, README/demo.md corrections**
- [x] **15. Pull request: final check (Release build 0 warnings, `dotnet test` 527/527 green), PROGRESS updated, PR `agent/project-7c5d012e` → `main` opened** (see step notes)
- [ ] Human tasks — see [Open human tasks](#open-human-tasks) (Docker run, CI activation, GIF/video, testnet run, publishing texts, NuGet, PR review/merge)

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
docker compose up --build                           # whole stack: shop :5080, service :5052, anvil :8545, postgres :5432
scripts/smoke-test.sh                               # S1 → S2 → webhook → forged webhook, against the running stack
```

Tree Work Preview runs `npm install && npm run dev`: `package.json` → `scripts/dev.sh`, which installs the
.NET 10 SDK into `~/.dotnet` if missing and runs the service on `0.0.0.0:$PORT`.

CI: workflow file is stored at `docs/ci/ci.yml` (see open points) — restore, build (Release), test (trx + coverage,
hang timeout), test report (check run + job summary), coverage summary, pack SDK, compose smoke test, on every push/PR.
Scenario → test map: [TESTS.md](TESTS.md). Only the E2E scenarios: `dotnet test tests/AcceptCrypto.IntegrationTests --filter "FullyQualifiedName~EndToEnd"`.
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

### Step 8 — public payment page with QR and live status (done)

Decisions: **D17**; contract: [API.md § Payment page](API.md#payment-page-fr-4-fr-29-fr-30).
Screenshots (real invoice on Anvil, scripted payer): `docs/images/payment-page-{pending,confirming,paid}.png`.

- `src/AcceptCrypto.Service/PaymentPage/`: `PaymentPageEndpoints` (`GET /pay/{token}`, `/pay/{token}/qr.svg`,
  `/pay/assets/{name}`, `GET /api/public/invoices/{token}` — in OpenAPI under tag "Public", no auth),
  `PublicInvoiceView` + `PublicInvoiceMapper` (allowlist view, confirmation progress from `ChainScanState.Head`,
  EIP-55 address, `Eip681`), `PaymentPageResolver` (token shape check, DB lookup, Development sample),
  `PaymentPageRenderer` (HTML template), `QrCodes` (QRCoder SVG), `PaymentPageAssets` + `Assets/pay.css`, `pay.js`
  (embedded resources), `PaymentPageOptions` (section `PaymentPage`). Wired in `Program.cs` incl. `AddRateLimiter`.
- Page shows network, token (+ contract), deposit address, exact amount, deadline with countdown, status; copy
  buttons; QR toggle "Wallet link" (EIP-681, remaining amount) / "Address only". Status texts per status: pending,
  confirming (n of N + progress bar), underpaid (remaining), paid, overpaid (excess), expired (with/without partial),
  paid_late, cancelled, and "deadline passed" while the expiry worker has not yet run.
- The scripted payer's public path (`api/public/invoices/{token}`, flat JSON with `address`/`amount`/`status`/
  `chain_id`/`token_address`/`expires_at`) now works: `pay http://host/pay/<token>` needs no API key.
- Tests (`IntegrationTests/Api/PaymentPageTests.cs`, 11): no order ID / metadata / invoice ID / idempotency key / sender
  in page, JSON or QR + exact JSON field set; page from the `payment_page_url` of POST /v1/invoices; unknown and
  malformed tokens → 404 (JSON Problem Details, HTML, QR); status JSON through pending → confirming (1 of 2) →
  underpaid (remaining 6, EIP-681 with 6000000) → paid; overpaid/cancelled/past deadline → no payment requested;
  security headers + CSP; HTML encoding of config; rate limit 429 (merchant API unaffected); scripted payer via the
  public endpoint; sample mode; EIP-681 format.
- Verified end to end (local PostgreSQL 15 + Anvil + scripted payer + Playwright/Chromium): invoice created via
  POST /v1/invoices, page opened from `payment_page_url`, `pay <url> --amount 10` → page shows "confirming 1 of 2",
  `pay mine 2` → "Partially paid, send the remaining 15.5", top-up + mine → "Payment received" — all with **one page
  load** and no JS errors. Whole solution green: 135 + 110 + 1 + 148 tests.

Try it:

```bash
# Preview / no DB: Development has PaymentPage:EnableSample=true
open http://localhost:$PORT/pay/sample?status=confirming     # pending|confirming|underpaid|paid|overpaid|expired|paid_late|cancelled
# Real cycle: scripts/local-chain.sh, then run the service with Chain__Enabled=true and a database, create an invoice
# (see Step 4 — merchant REST API), open its payment_page_url and run:
dotnet run --project src/AcceptCrypto.Payer -- <payment_page_url> --amount 10 && dotnet run --project src/AcceptCrypto.Payer -- mine 2
```

Notes for next steps: demo shop (step 10) redirects the customer to `payment_page_url`; compose (step 11) sets
`PaymentPage__MerchantDisplayName=Demo Shop` and `Api__PublicBaseUrl` to the host-visible URL; README (step 12) can use
`docs/images/payment-page-*.png` (or `/pay/sample`) — Playwright's `waitForFunction` is blocked by the page CSP, poll
`textContent` instead.

### Step 9 — SDK `AcceptCrypto.NET` for ASP.NET Core (done)

Decisions: **D18**; usage: [src/AcceptCrypto.Sdk/README.md](../src/AcceptCrypto.Sdk/README.md) (also the package README).

- `src/AcceptCrypto.Sdk` (package ID `AcceptCrypto.NET`, `0.1.0-alpha`, net10.0, no third-party dependencies):
  - `AcceptCryptoServiceCollectionExtensions.AddAcceptCrypto()` (section `AcceptCrypto`; overloads with
    `Action<AcceptCryptoOptions>` / `IConfiguration`), returns `IHttpClientBuilder`; `AcceptCryptoOptions` + validator.
  - `IAcceptCryptoClient` / `AcceptCryptoClient` (typed HttpClient): `CreateInvoiceAsync` (idempotency key, replay
    flag), `GetInvoiceAsync`, `ListInvoicesAsync` / `ListAllInvoicesAsync`, `CancelInvoiceAsync`,
    `ListUnmatchedPaymentsAsync` / `ListAllUnmatchedPaymentsAsync`, `ResendEventAsync`. Models in `Models/`,
    tolerant snake_case enums (`Json/AcceptCryptoJson.cs`), `UsdcAmount`.
  - `Errors.cs`: `AcceptCryptoException` → `AcceptCryptoConnectionException`, `AcceptCryptoApiException` →
    Validation / Authentication / NotFound / Conflict (`CurrentStatus`) / IdempotencyKeyReused (`InvoiceId`);
    `AcceptCryptoErrorCodes`.
  - `Webhooks/`: `AcceptCryptoWebhookVerifier` (verify, `VerifyAndParse`, `ComputeSignature(Header)` for tests),
    `AcceptCryptoEvent` + `InvoiceStatusChangedEvent` / `PaymentRevertedEvent` / `UnknownAcceptCryptoEvent`,
    `AcceptCryptoEventTypes`, `IWebhookEventDeduplicator` + `InMemoryWebhookEventDeduplicator`,
    `MapAcceptCryptoWebhook(...)` + `IAcceptCryptoWebhookHandler` (namespace `Microsoft.AspNetCore.Builder`).
- Tests:
  - `tests/AcceptCrypto.Sdk.Tests` (93): client against a stub `HttpMessageHandler` (request shape, headers, query
    building, cursor following, uint256 amounts, unknown enum values, every Problem Details → exception mapping,
    non-JSON errors, transport errors, cancellation); verifier (the three reference vectors of webhooks.md, valid,
    forged, altered body, expired/future timestamp + boundary, configurable tolerance, rotation both directions,
    unknown schemes, malformed headers); event parsing (documented payload, every status event, payment.reverted,
    unknown type, malformed bodies, amounts as numbers refused); endpoint on a TestServer (valid → handler, forged /
    replayed / unsigned → 400, rotation, duplicate → handled once, handler failure → 500 then retry processed,
    413, handler class, fail-fast mapping); DI binding/validation; `UsdcAmount`; deduplicator.
  - `tests/AcceptCrypto.IntegrationTests/Api/SdkContractTests.cs` (4): SDK client against the **real service** (all
    operations, idempotent replay, typed errors incl. 400/401/404/409/422) and SDK verification of webhooks sent by
    the **real dispatcher** during secret rotation.
- Verified: whole solution 135 + 110 + 93 + 152 tests green against local PostgreSQL 15
  (`ACCEPTCRYPTO_TEST_POSTGRES`); `dotnet pack` produces `AcceptCrypto.NET.0.1.0-alpha.nupkg` (+ `.snupkg`) with
  README, license, repository commit and SourceLink; the README's minimal example compiles.

```bash
dotnet test tests/AcceptCrypto.Sdk.Tests
dotnet pack src/AcceptCrypto.Sdk -c Release -o artifacts      # → artifacts/AcceptCrypto.NET.0.1.0-alpha.nupkg (+ .snupkg)
```

Notes for next steps: the demo shop (step 10) uses `AddAcceptCrypto()` with section `AcceptCrypto`
(`BaseUrl`, `ApiKey`, `WebhookSecret` = the service's `Merchant:*` values) and
`app.MapAcceptCryptoWebhook("/webhooks/acceptcrypto", …)`; to show FR-26 it can post a forged / replayed webhook to
itself (`AcceptCryptoWebhookVerifier.ComputeSignatureHeader` signs test payloads). Publishing to nuget.org is not
done (needs an API key from the owner; CI already uploads the `.nupkg` as an artifact).

### Step 10 — Demo shop and one-command stack (done)

Decisions: **D19**; walkthrough of every scenario: [demo.md](demo.md).

- `src/AcceptCrypto.DemoShop` (SDK only): catalog of 3 products → `POST /checkout` creates the order, calls
  `CreateInvoiceAsync` (idempotency key `order-<id>`, optional 2-minute lifetime for S4) and redirects to the payment
  page; `/orders`, `/orders/{id}` (order + its webhooks + the service's view via `GetInvoiceAsync`), `/webhooks`
  (webhook log incl. rejected/duplicates), `/api/orders` (JSON for scripts), `/health`.
  Webhooks: `MapAcceptCryptoWebhook("/webhooks/acceptcrypto", …)`; `Shop/ShopStore.cs` = JSON-file store (atomic
  writes, `Shop:DataPath`) + persistent `IWebhookEventDeduplicator` + `IAcceptCryptoWebhookObserver`; stale (older)
  events don't move an order back. FR-26 demo: `POST /demo/attacks/{forged|tampered|replay|stale}` (`Shop/WebhookAttacks.cs`).
- SDK: new optional `IAcceptCryptoWebhookObserver` (rejected / duplicate webhooks), test added; README section.
- Payer: an explicit `--service` / `ACCEPTCRYPTO_SERVICE_URL` wins over the host of a payment page URL (Docker); test added.
- `Dockerfile` (targets `service`, `demoshop`, `payer`), `.dockerignore`, `docker-compose.yml` (postgres, anvil with
  2 s blocks, one-off `deploy`, service, demoshop, `payer` in profile `tools`; test-only secrets inline with a
  warning; webhook URL → `http://demoshop:8080/...`, `demoshop` on the SSRF allowlist, 5 s→30 s retries for 30 min,
  5 confirmations). Scripts: `scripts/pay.sh` (payer container, or `dotnet run` with `PAY_USE_DOTNET=1`),
  `scripts/webhook-attack.sh <kind>`, `scripts/smoke-test.sh`. `docs/ci/ci.yml` got a `compose-smoke` job.
- Tests: `tests/AcceptCrypto.DemoShop.Tests` (8, `WebApplicationFactory` + fake `IAcceptCryptoClient`): checkout →
  invoice + redirect + idempotency key, short lifetime, service down → `invoice_failed`/502, signed webhook → paid and
  duplicate not applied, older event ignored, forged/tampered/stale rejected + replay = duplicate + logged, orders and
  processed IDs survive a restart, pages render. Whole solution: 135 + 111 + 94 + 8 + 152 = 500 green (local
  PostgreSQL 15 + Anvil).

**Verification of the stack (this container has no usable Docker: the daemon starts but cannot create containers or
even unpack image layers — "mount namespace: operation not permitted").** What was done instead:
- `docker compose config -q` (Compose v2.32) — the file is valid; the foundry `v1.9.0` tag and its entrypoint
  (`/bin/sh -c`, hence the explicit `entrypoint: ["anvil"]`) were checked in the ghcr.io registry.
- The Dockerfile's restore/publish commands were run on a copy containing only what the Dockerfile copies
  (`global.json`, `Directory.*.props`, `.editorconfig`, `src/`) — all three publish cleanly.
- The stack was run **natively with the exact environment of docker-compose.yml** (generated from the file; only host
  names mapped to localhost): Anvil `--block-time 2`, payer `setup` (deploy), the published service (Production
  environment + compose env), the published shop. Results:
  - **S1→S2**: shop checkout → 302 to `http://localhost:5052/pay/<token>` → payer (with the compose payer env, i.e.
    `ACCEPTCRYPTO_SERVICE_URL` override) → payment page `confirming` → `paid`; shop receives `invoice.confirming` and
    `invoice.paid`, order `paid`. `scripts/smoke-test.sh` passes in ~14 s.
  - **S3** underpay 1 → `underpaid` (remaining 4) → top-up 4 → `paid`. **S4** 2-minute invoice → `expired` → paid →
    `paid_late` (shop order `paid_late`).
  - **S5** shop stopped, order paid → service retries (5 s, 10 s, 20 s …) → shop restarted → `invoice.confirming`
    delivered on attempt 4, order `paid`.
  - **FR-8/FR-9** service stopped, transfer sent, service restarted → invoice `paid`, amount counted once.
  - **FR-10** pay + `pay reorg 4` within the confirmation window → `payment.reverted` + `invoice.pending`, never `paid`.
  - **FR-26** forged/tampered → 400 SignatureMismatch, stale → 400 TimestampOutsideTolerance, replay → duplicate
    (200, not applied); all in the webhook log.
  - **S7** transfer to the address of a paid invoice → `/v1/unmatched-payments` lists it (`invoice_closed`).
- Screenshots: `docs/images/demo-shop-*.png`.
- **Open:** a real `docker compose up --build` run still has to happen on a machine with Docker — the `compose-smoke`
  CI job does exactly that once CI is activated (see the CI open point), or run `docker compose up -d --build &&
  scripts/smoke-test.sh` locally.

### Step 11 — end-to-end tests, observability, CI (done)

Decisions: **D20**. Scenario/requirement → test table: [TESTS.md](TESTS.md). Metrics/health/logs: [observability.md](observability.md).

- `tests/AcceptCrypto.IntegrationTests/EndToEnd/`:
  - `EndToEndHarness` — the real `Program` host (API, payment page, scanner, expiry worker, webhook dispatcher all
    running) on a fresh PostgreSQL DB + reset Anvil (clock set to wall time), SDK typed client, stop/start/restart of
    the service on the same DB; `Eventually` polling with deadlines and "last seen" messages.
  - `MerchantApp` — a merchant ASP.NET Core app on the SDK (`AddAcceptCrypto` + `MapAcceptCryptoWebhook`) on Kestrel
    (fixed port, can be down), records handled/rejected/duplicate webhooks and raw requests; shiftable clock for replay.
  - `ScenarioTests` (10): S1, S2, S3 (under/over/split + top-up), S4 (expired + paid_late), S5 (merchant down → delivered
    when back), S6 (filters, cursor, transfers + webhook history), S7 (payments to paid/cancelled invoices → unmatched),
    FR-8/FR-9 (stop during a payment + payment while down → found once; second restart adds nothing), FR-10 (forced
    reorg → `payment.reverted` + `invoice.pending`, never paid), FR-26 (forged, tampered, unsigned → 400; immediate
    replay → duplicate, not re-handled; late replay → 400).
  - `ObservabilityTests` (2, own non-parallel collection): `/metrics` shows lag, queue, retry/delivered attempts,
    HTTP meters; `/health/ready` shows database, rpc, chain-scanner, webhooks.
- Observability (`src/AcceptCrypto.Service/Observability`): OpenTelemetry metrics + Prometheus exporter on `/metrics`
  (service meters + ASP.NET Core/Kestrel/HttpClient/runtime), `Observability:MetricsEnabled|MetricsPath`;
  `Health/WebhookQueueHealthCheck` (`webhooks`, Degraded on failed events or a stale queue). Logs were already
  structured JSON (step 4); documented.
- Test hosts outside the E2E harness set `Observability:MetricsEnabled=false` (OpenTelemetry's per-host preallocation
  exhausted the 1.5 GB agent container with ~150 hosts; see D20).
- CI template `docs/ci/ci.yml`: image pre-pull, `--blame-hang-timeout 5m`, `dorny/test-reporter@v2` (trx → check run +
  job summary, `checks: write`), ReportGenerator coverage summary, artifacts (results + coverage). Linted with actionlint.
- README: links to TESTS.md and observability.md; API.md: `/metrics`, webhooks health check.

Verified here (no Docker daemon in the agent container → `ACCEPTCRYPTO_TEST_POSTGRES` against local PostgreSQL 15 and
the `anvil` binary): whole solution green — 135 domain + 111 infrastructure + 94 SDK + 8 demo shop + 164 integration
= 512 tests; the integration suite was run 3 more times in a row, all green (one flaky metrics assertion found on the
way and fixed by isolating that collection). Release build clean, SDK packs. Production run: JSON logs, `/metrics`,
`/health/ready` checked by hand; Preview (`npm run dev`) still starts, `/health` Healthy.

### Step 12 — README and architecture documentation (done)

- `README.md` rewritten for the Upwork client and for a stranger: value in the first line + CI badge, no-mainnet
  warning, **payment-cycle GIF** (`docs/images/payment-cycle.gif`, 6 frames: shop → payment page pending →
  confirming → paid → shop order paid → webhook log), Docker-only quick start, a walk-through of S1–S7 + FR-8/9,
  FR-10, FR-26 with exact commands, architecture diagram (Mermaid flowchart: service API / payment page / domain /
  scanner / expiry worker / outbox dispatcher, PostgreSQL, chain, SDK, demo shop, payer), invoice state diagram incl.
  `cancelled` and `expired → confirming` (D6, D11), "How the hard parts are solved" table (double counting, catch-up,
  reorgs, lost webhooks, atomicity, concurrency, precision, SSRF, forged webhooks) each linking the proving test file
  and the TESTS.md section, SDK minimal example, decisions summary → DECISIONS.md, roadmap (spec's out-of-scope list).
- A link checker run (all relative links exist, all GitHub heading anchors resolve, every test name cited exists in
  `tests/`) — repeat it if headings change.
- **Fix found while following the README:** `pay reorg` used `anvil_reorg`, which re-mines the replaced blocks with
  older timestamps; after a few reorgs the chain clock lagged the wall clock by ~35 s and a "pay after expiry" (S4)
  ended `paid` instead of `paid_late` (block timestamp before the deadline — correct per the rule, wrong for the demo).
  `PayerApp.ReorgAsync` now re-syncs the clock (`evm_setNextBlockTimestamp(now + 1 s)` when the head is behind).
- How the README was checked: **Docker cannot run containers in the agent container** (dockerd starts only with
  `--iptables=false --bridge=none`, and then `docker run` fails: "Error creating mount namespace … operation not
  permitted"). So the README was followed on a clean clone (`git clone` → `/tmp/clean`) with the compose stack's
  settings reproduced natively: PostgreSQL 15 (fresh database), `anvil --block-time 2 --chain-id 31337`, payer
  `setup`, the service with `Chain__Confirmations=5`, demo retry schedule and `Invoices__MinLifetime=00:01:00`, the shop.
  Verified: S1 curl create + idempotent replay (200), S2 via the shop in Playwright (GIF frames), S3 underpaid → top-up
  → paid, S4 expired → `paid_late`, S5 shop killed → 4 attempts → delivered after restart, FR-8/9 service killed,
  paid by address, restarted → paid with 1 transfer, FR-10 pay + `reorg 4` ≈ 3 s apart → `payment.reverted`,
  `invoice.pending`, S7 `--to` a paid invoice's address → unmatched `invoice_closed`, FR-26 `webhook-attack.sh forged`
  → 400, `replay` → 200 duplicate, manual curl → 400. `docker compose config -q` passes.
- FR-10 timing: with `scripts/pay.sh` falling back to `dotnet run` (no Docker) the second call starts ~10 s later — too
  late for 5 confirmations at 2 s blocks. In Docker the payer container starts in ~2–3 s (assumed, not measured here);
  without Docker use `scripts/local-chain.sh` (no `--block-time`, blocks only on transactions) as docs/demo.md says.

### Step 13 — Base Sepolia guide and packaging drafts (done)

What was added (decisions in DECISIONS.md **D21**):

- **Profile** `src/AcceptCrypto.Service/appsettings.BaseSepolia.json` (`ASPNETCORE_ENVIRONMENT=BaseSepolia`): chain
  84532, Circle test USDC `0x036CbD53842c5426634e7929541eC2318f3dCF7e` (source: Circle's
  [USDC contract addresses](https://developers.circle.com/stablecoins/usdc-contract-addresses), Testnet → Base
  Sepolia; checked on-chain: `decimals()` 6, `symbol()` USDC), N = 60, `MaxBlockRange` 200, `ReorgRollbackDepth` 300,
  poll 4 s, `MaxAmount` 100. **No RPC URL and no secrets** in it: `Chain__RpcUrl`, `Merchant__*` come from env.
- **`docker-compose.base-sepolia.yml`** (project `acceptcrypto-base-sepolia`: postgres + service + demo shop, no
  Anvil) + **`base-sepolia.env.example`**; the real `base-sepolia.env` is git-ignored by `*.env`. Required variables
  use `${VAR:?…}`, so a missing secret fails fast. `docker compose … config -q` passes.
- **`pay xpub [new]`** (scripted payer, offline): xpub at `m/44'/60'/0'/0` + first deposit addresses from
  `MERCHANT_MNEMONIC` (env, never echoed) or a freshly generated 12-word mnemonic; refuses the public test mnemonics.
  Tests: `Infrastructure.Tests/LocalChain/MerchantXpubTests` (cross-checked with `XpubDepositAddressDeriver`).
- **Demo shop:** `Shop:PayerCommand` empty → the local "scripted payer" hint is hidden (testnet video). Test
  `DemoShopTests.Testnet_profile_hides_the_scripted_payer_hint`.
- **docs/testnet.md:** RPC endpoint, xpub, faucets (Base docs faucet list, Circle faucet), start (Docker and
  `dotnet run --no-launch-profile`), paying from MetaMask, what to watch out for, the 1–2 min video script.
- **docs/marketing/:** `README.md` (index + facts used), `upwork-portfolio-card.md`, `devto-article.md` (structure +
  full draft from the actual code), `project-catalog-offer.md` (packages with `[PRICE]`/`[DAYS]` placeholders).
  No facts about the author, no invented numbers.

Verified here (agent container, no Docker daemon): the service started with `ASPNETCORE_ENVIRONMENT=BaseSepolia`
against the **public Base Sepolia RPC** and a local PostgreSQL 15: profile loaded (log: chain 84532, 60
confirmations, scanner enabled), an invoice created via the API got deposit address #0 = the address printed by
`pay xpub`, `/health/ready` Healthy with `lag_blocks` 0–25, restart resumed from the checkpoint. Screenshot:
`docs/images/base-sepolia-payment-page.png`. **Not verified:** an actual MetaMask payment on Base Sepolia (needs a
funded wallet — human task).

Facts measured while writing (Nov 18, 2026): the public RPC `https://sepolia.base.org` rejects `eth_getLogs` over
more than 200 blocks ("eth_getLogs is limited to a 200 range"); 1,000 Base Sepolia blocks took 200 s (≈ 5 blocks/s,
not the 2 s blocks assumed in D14) — hence N = 60 ≈ 12 s.

Tests (all green here): Domain 135, DemoShop 9, Sdk 94, Infrastructure 120, Integration 164 (local PostgreSQL 15 via `ACCEPTCRYPTO_TEST_POSTGRES` + `anvil` binary; no Docker daemon).

### Step 14 — Definition of done on a clean clone (done)

**Setup.** `git clone --branch agent/project-7c5d012e` into an empty directory, then only README / docs/demo.md.
**Docker could not run in the agent sandbox:** `dockerd` starts only with `--iptables=false`, and pulling the first
image fails with `Error creating mount namespace before pivot: operation not permitted` (no mount namespaces in the
container). So the stack was started with the documented **no-Docker path** ([demo.md → Without Docker](demo.md#without-docker)):
PostgreSQL 15 on localhost, `anvil --chain-id 31337 --block-time 2`, `pay setup`, `dotnet run` service + shop — the same
settings as `docker-compose.yml` (after this step `appsettings.Development.json` mirrors them; on the first pass they
were passed as env vars). The payer ran as `PAY_USE_DOTNET=1 scripts/pay.sh …`; "stop the service/shop" = kill the
`dotnet run` process. **The compose path itself still needs one run under real Docker** (CI job `compose-smoke`, or
the human: `docker compose up --build && scripts/smoke-test.sh`).

**Timing.** Clone → stack healthy: 208 s; clone → first shop order **paid** (webhook applied in the shop): **269 s
(≈ 4.5 min)** — including ~100 s lost on the failed Docker attempt and a 31 s `dotnet build`. Target ≤ 10 min ✅ for this
path. Under Docker the README estimates 3–5 min for the first image build, then < 1 min to `paid`; not measured here.

| DoD item | Result | How it was checked / comment |
| --- | --- | --- |
| S1 invoice via API, page opens | ✅ | README `curl` → 201 with `payment_page_url`, page 200; same `Idempotency-Key` → 200 + `Idempotent-Replayed: true`, same ID. **Found:** replay body differed from the 201 in timestamp precision (`.9175839` vs `.917583`) → fixed (below), re-checked: byte-identical. Also via the shop: *Buy* → redirected to the payment page |
| S2 pay → `paid`, shop webhook, page shows it | ✅ | Shop order (T-shirt, 25 USDC) paid by the scripted payer; ~19 s later page *Payment received*, shop order `paid`, shop log `invoice.confirming` + `invoice.paid` handled (screenshots in the step log). `scripts/smoke-test.sh` also: All good |
| S3 underpaid / overpaid / split | ✅ | `--amount 1` of 10 → `underpaid` (remaining 9); top-up `--amount 9` → `paid`; `--amount 100` of 10 → `overpaid`; `--split 2` → `paid` |
| S4 expired / paid_late | ✅ | 60 s invoices → `expired` (expiry sweep within a few seconds of the deadline); paying one afterwards → `paid_late`. Re-checked after a forced reorg (chain clock re-sync): still `paid_late` |
| FR-8/FR-9 service stopped during a payment | ✅ | (a) service down, paid by address, restarted → `paid`; (b) paid, service killed while `confirming`, 20 s down, restarted → `paid`. Both: 1 transfer, events exactly `invoice.confirming`, `invoice.paid` (not counted twice) |
| FR-10 forced reorg | ✅ (after fix) | First pass with the README one-liner (`pay.sh <url> && pay.sh reorg 4`) worked; on the re-run it came **too late** (invoice reached `paid` — the 4 dropped blocks no longer contained the tx): starting the payer twice with `dotnet run` costs ~6 s each vs ≈ 10 s for 5 confirmations. **Fix:** `pay <url> --then-reorg` (below). Re-checked twice: `invoice.confirming` → `payment.reverted` → `invoice.pending`, never `paid`; shop received `payment.reverted` |
| S5 shop down → webhook delivered later | ✅ | Shop process killed, order paid; service log: attempts 2, 3, 4 … failed (connection refused), retried 5 s → 30 s; shop restarted → `invoice.confirming` and `invoice.paid` handled in order, order `paid` |
| FR-26 forged / replayed webhook rejected | ✅ | `scripts/webhook-attack.sh`: forged, tampered → 400 "No valid v1 signature."; stale → 400 "…outside the tolerance window."; replay → 200 + shop log "is a duplicate; skipped" (not applied); raw `curl` with `v1=00` → 400. **Found:** README promised the bodies `SignatureMismatch` / `TimestampOutsideTolerance` (those are the reason names in the shop's webhook log, not the HTTP response) → README/demo.md wording fixed |
| S7 unmatched transfer listed | ✅ | 1 USDC to a paid invoice's address → `GET /v1/unmatched-payments`: `reason: invoice_closed` + `invoice_id`. A transfer to a random address is **not** listed — by design (D14: `eth_getLogs` filters on deposit addresses, `unknown_address` unreachable); README only promises the closed-invoice case |
| (S6) list + history | ✅ | `?status=paid&limit=2` → 2 items + `next_cursor`; details show transfers and webhook events; no API key → 401; `/openapi/v1.json` 200; `/metrics` exposes `acceptcrypto_*` |
| Tests green in CI | ❌ (not observable) / ✅ locally | **No GitHub Actions run exists**: the workflow is still only at `docs/ci/ci.yml` (agent token cannot push `.github/workflows/`), the GitHub API reports 0 runs. Locally on the clean clone, the CI steps: Release build 0 warnings/errors, `dotnet test` **527/527 passed** (Domain 135, Infrastructure 123, SDK 94, Shop 9, Integration 166; PostgreSQL via `ACCEPTCRYPTO_TEST_POSTGRES`, Anvil binary), `dotnet pack` OK. Owner action: copy the workflow, check the first run |
| README enough to run everything | ✅ (after fixes) | Every scenario ran from README/demo.md commands only. Fixed along the way: FR-26 response wording; FR-10 timing (`--then-reorg`); no-Docker pointer in Quick start; demo.md "Without Docker" now uses `anvil --block-time 2` + `pay setup` (with `local-chain.sh` blocks only come with transactions, so confirmations never accrue) and states that the Development settings match the compose stack |

**Fixes in this step.**

- `StorageTimeProvider` (Infrastructure/Persistence): the system clock truncated to microseconds (PostgreSQL
  `timestamptz` precision); default `TimeProvider` of persistence, webhooks and scanner registrations. The 201 of a
  create and its idempotent replay (FR-2) — and any in-memory response vs a later GET — are now identical. Tests:
  `StorageTimeProviderTests`.
- `appsettings.Development.json` (service): `Chain:Confirmations` 5, webhook retries 5 s → 30 s, 60 attempts, 30 min —
  as in `docker-compose.yml`, so the walk-through behaves the same without Docker (tests use the `Testing` environment,
  unaffected).
- Payer `--then-reorg` (FR-10 demo): pays, polls the public status until `confirming` (≤ 30 s), then reorgs from the
  payment's first block (depth = head − block + 1) with the same chain-clock re-sync as `reorg`; refuses (exit 1) if
  the invoice is already past `confirming`. Tests in `ScriptedPayerTests`. README, demo.md, pay.sh and compose comments
  use it; `reorg N` stays for the by-hand variant.
- README/demo.md: FR-26 responses, no-Docker pointer, demo.md "Without Docker" rewritten.

**Not changed (noted):** the sandbox's own PostgreSQL had to be stopped to free port 5432 for the compose attempt —
a stranger with a local PostgreSQL hits the same; the README already says the ports must be free.

### Step 15 — Pull request (done)

- Working tree clean; `dotnet build AcceptCrypto.NET.slnx -c Release --no-incremental`: 0 warnings, 0 errors.
- `dotnet test -c Release` (PostgreSQL 15 via `ACCEPTCRYPTO_TEST_POSTGRES`, Anvil binary from `~/.foundry/bin`):
  **527/527 passed** — Domain 135, Infrastructure 123, SDK 94, DemoShop 9, Integration 166 (≈ 2 min).
- Pull request opened from `agent/project-7c5d012e` into `main` with the reviewer summary (what was built, decisions,
  how to run/check, Definition of done results, limitations). No code changes in this step.

## Open human tasks

Everything the agents could not do (no usable Docker, no `workflow` token scope, no wallet / publishing accounts).
Details in "Open points" below and in [testnet.md](testnet.md) / [marketing/](marketing/).

1. **Review and merge the PR** (`agent/project-7c5d012e` → `main`).
2. **Activate CI:** copy `docs/ci/ci.yml` to `.github/workflows/ci.yml` on GitHub, check the first run is green
   (build-test with Testcontainers + `compose-smoke`); the README badge then turns green.
3. **Run the Docker stack once:** `docker compose up --build`, follow the README quick start,
   `scripts/smoke-test.sh` (verified natively only so far).
4. **GIF:** optionally re-record `docs/images/payment-cycle.gif` as a real screen recording (current: 6-frame slideshow).
5. **Testnet run** on Base Sepolia per [testnet.md](testnet.md) (own xpub via `pay xpub new`, MetaMask, faucet ETH +
   Circle test USDC); report differences from the guide.
6. **Video** (1–2 min, script at the end of testnet.md); check that no secret is visible; optionally link it in the README.
7. **Publishing texts:** fill the `[…]` placeholders in [marketing/](marketing/), take the screenshots, publish the
   Upwork portfolio card, the dev.to article and the Project Catalog offer.
8. **NuGet:** publish the `AcceptCrypto.NET` SDK package to nuget.org (README says "not yet on nuget.org").

## Open points

- **For the human (packaging, step 12):** (1) run `docker compose up --build` once on a machine with Docker and
  follow the README quick start — the stack has been verified natively only (see step 12); if the FR-10 one-liner is
  too slow there, raise `Chain__Confirmations` in `docker-compose.yml`. (2) Optionally re-record
  `docs/images/payment-cycle.gif` as a real screen recording (current one is a 6-frame slideshow of a local run).
  (3) Publish the SDK to nuget.org (README says "not yet on nuget.org").

- **For the human (step 13 — testnet and packaging):**
  1. Testnet run per [testnet.md](testnet.md): RPC endpoint (public or provider key), `pay xpub new` → own test
     xpub, MetaMask test account with faucet ETH + Circle test USDC, `docker compose -f docker-compose.base-sepolia.yml
     --env-file base-sepolia.env up --build`, pay the sticker pack (5 USDC) from MetaMask. Report back anything that
     differs from the guide (e.g. MetaMask EIP-681 QR behaviour, faucet limits).
  2. Record the 1–2 min video (script at the end of testnet.md); check no secret is visible.
  3. Review and finish the drafts in [marketing/](marketing/): fill every `[…]` placeholder (role, prices, delivery
     times, series, links), check the claims, take the listed screenshots, publish (Upwork card, dev.to, Project
     Catalog).
  4. Optionally add the video link to the README.

- **Definition of done (step 14):** all scenario items ✅ on a clean clone via the no-Docker path; open: one run of the
  compose path under real Docker and the first green CI run on GitHub (both need the human / CI, see below).
- **Docker stack not yet run under real Docker** (no usable Docker in the agent container, see steps 10 and 14): run
  `docker compose up -d --build && scripts/smoke-test.sh` once, or activate CI (job `compose-smoke`).

- **CI not yet observed green on GitHub** (step 11): everything passes locally against PostgreSQL + Anvil binary, but the
  Testcontainers path (Docker on `ubuntu-latest`) and the compose smoke job only run once the workflow is activated.
  After copying `docs/ci/ci.yml`, check the first run (expected ~6–8 min for build-test).
- **CI activation (manual, owner):** the platform's GitHub token lacks the `workflow` scope, so the workflow cannot be
  pushed to `.github/workflows/`. The owner copies `docs/ci/ci.yml` to `.github/workflows/ci.yml` on GitHub.
  Agents must not commit files under `.github/workflows/` (the push is rejected). The README badge points to `ci.yml`.

- CI badge added to README (repo: github.com/alena-dev-soft/accept-crypto-net).
- Default N confirmations (D4): 2 (local Anvil; compose 5); Base Sepolia profile 60 ≈ 12 s at the measured ≈ 5 blocks/s — see D21. Open: unmatched rows from reorged blocks are not removed (D14 known limitation).
- Payment page UI technology: chosen in step 8 (D17: server-rendered HTML + vanilla JS, polling).
- Step 3 follow-ups: step 4 maps `KeyReusedWithDifferentRequest` to a Problem Details error and computes the request
  fingerprint (e.g. SHA-256 of the canonical create request) (step 6 done: `Chain:ChainId` feeds
  `Persistence:ChainId`, `InvoiceClosed` transfers are recorded as unmatched); step 7 done (claiming with `FOR UPDATE SKIP LOCKED`, per-invoice order, D16); step 11 compose sets
  `ConnectionStrings__AcceptCrypto` and `Persistence__MigrateOnStartup=true`. DB/RPC readiness checks exist since step 4 (`/health/ready`).
