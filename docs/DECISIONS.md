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
