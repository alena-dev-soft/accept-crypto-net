# Progress — AcceptCrypto.NET

Spec: `AcceptCrypto.NET_SPEC.md` (project file, not in the repo). Decisions: [DECISIONS.md](DECISIONS.md).

## Plan

- [x] **1. Solution skeleton, decision log, CI and progress file** (this file, DECISIONS.md, CI workflow in `docs/ci/ci.yml`, /health stub)
- [x] **2. Domain: money (USDC base units), invoice aggregate, status machine incl. `cancelled`, unit tests** (see step notes, DECISIONS D11)
- [x] **3. Persistence: PostgreSQL / EF Core, migrations, outbox, idempotency keys, optimistic concurrency** (see step notes, DECISIONS D12)
- [ ] 4. Merchant API: create/get/list/cancel invoices, unmatched payments, resend event; API key auth, Problem Details, OpenAPI
- [ ] 5. xpub address derivation (BIP32 non-hardened → EIP-55 address)
- [ ] 6. Chain scanner: eth_getLogs, checkpoint, chunking, confirmations, reorg handling, expiry
- [ ] 7. Webhooks: signing (HMAC-SHA256 over `{timestamp}.{body}`), dispatcher with backoff, secret rotation, SSRF guard
- [ ] 8. Payment page: instructions + QR + live status
- [ ] 9. SDK: DI registration, typed client, webhook verification, typed events, README, tests
- [ ] 10. Demo shop + scripted payer (`pay <invoice>`) + mock USDC on Anvil
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

## Open points

- **CI activation (manual, owner):** the platform's GitHub token lacks the `workflow` scope, so the workflow cannot be
  pushed to `.github/workflows/`. The owner copies `docs/ci/ci.yml` to `.github/workflows/ci.yml` on GitHub.
  Agents must not commit files under `.github/workflows/` (the push is rejected). The README badge points to `ci.yml`.

- CI badge added to README (repo: github.com/alena-dev-soft/accept-crypto-net).
- Exact default N confirmations per chain (D4) is set when the scanner is built.
- Payment page UI technology (D8) is chosen in step 8.
- Step 3 follow-ups: step 4 maps `KeyReusedWithDifferentRequest` to a Problem Details error and computes the request
  fingerprint (e.g. SHA-256 of the canonical create request); step 6 sources `Persistence:ChainId` from the chain
  config and records `InvoiceClosed` transfers via `IUnmatchedPaymentRepository`; step 7 claims due outbox rows
  (consider `FOR UPDATE SKIP LOCKED`) and should keep per-invoice event order; step 11 compose sets
  `ConnectionStrings__AcceptCrypto` and `Persistence__MigrateOnStartup=true`. A DB health check (tagged "ready") is
  still to be added once the stack has a database (keeps the Preview's `/health` green without one).
