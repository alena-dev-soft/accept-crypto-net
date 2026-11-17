# Progress — AcceptCrypto.NET

Spec: `AcceptCrypto.NET_SPEC.md` (project file, not in the repo). Decisions: [DECISIONS.md](DECISIONS.md).

## Plan

- [x] **1. Solution skeleton, decision log, CI and progress file** (this file, DECISIONS.md, ci.yml, /health stub)
- [ ] 2. Domain: money (USDC base units), invoice aggregate, status machine incl. `cancelled`, unit tests
- [ ] 3. Infrastructure: PostgreSQL / EF Core, migrations, outbox, idempotency keys
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

CI: `.github/workflows/ci.yml` — restore, build (Release), test (trx + coverage), pack SDK, on every push/PR.
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

## Open points

- CI badge added to README (repo: github.com/alena-dev-soft/accept-crypto-net).
- Exact default N confirmations per chain (D4) is set when the scanner is built.
- Payment page UI technology (D8) is chosen in step 8.
