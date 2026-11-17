# Progress — AcceptCrypto.NET

Spec: `AcceptCrypto.NET_SPEC.md` (project file, not in the repo). Decisions: [DECISIONS.md](DECISIONS.md).

## Plan

- [x] **1. Solution skeleton, decision log, CI and progress file** (this file, DECISIONS.md, CI workflow in `docs/ci/ci.yml`, /health stub)
- [x] **2. Domain: money (USDC base units), invoice aggregate, status machine incl. `cancelled`, unit tests** (see step notes, DECISIONS D11)
- [x] **3. Persistence: PostgreSQL / EF Core, migrations, outbox, idempotency keys, optimistic concurrency** (see step notes, DECISIONS D12)
- [ ] 4. Merchant API: create/get/list/cancel invoices, unmatched payments, resend event; API key auth, Problem Details, OpenAPI
- [x] **Local chain: Anvil, mock USDC, xpub derivation (former 5), scripted payer, chain test helpers** (see step notes, DECISIONS D13)
- [x] **6. Chain scanner: eth_getLogs, checkpoint, chunking, confirmations, reorg handling, unmatched, expiry** (see step notes, DECISIONS D14)
- [ ] 7. Webhooks: signing (HMAC-SHA256 over `{timestamp}.{body}`), dispatcher with backoff, secret rotation, SSRF guard
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
dotnet run --project src/AcceptCrypto.Service      # GET /health -> "Healthy"
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

## Open points

- **CI activation (manual, owner):** the platform's GitHub token lacks the `workflow` scope, so the workflow cannot be
  pushed to `.github/workflows/`. The owner copies `docs/ci/ci.yml` to `.github/workflows/ci.yml` on GitHub.
  Agents must not commit files under `.github/workflows/` (the push is rejected). The README badge points to `ci.yml`.

- CI badge added to README (repo: github.com/alena-dev-soft/accept-crypto-net).
- Default N confirmations (D4): 2 (local Anvil), recommend 3+ on Base Sepolia — see D14. Open: unmatched rows from reorged blocks are not removed (D14 known limitation).
- Payment page UI technology (D8) is chosen in step 8.
- Step 3 follow-ups: step 4 maps `KeyReusedWithDifferentRequest` to a Problem Details error and computes the request
  fingerprint (e.g. SHA-256 of the canonical create request) (step 6 done: `Chain:ChainId` feeds
  `Persistence:ChainId`, `InvoiceClosed` transfers are recorded as unmatched); step 7 claims due outbox rows
  (consider `FOR UPDATE SKIP LOCKED`) and should keep per-invoice event order; step 11 compose sets
  `ConnectionStrings__AcceptCrypto` and `Persistence__MigrateOnStartup=true`. A DB health check (tagged "ready") is
  still to be added once the stack has a database (keeps the Preview's `/health` green without one).
