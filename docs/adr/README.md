# Architecture Decision Records

Short records of the decisions behind AcceptCrypto.NET. One file per decision, format:
**Status · Context · Decision · Consequences**. A record marked *Proposed* is the default we
build on until the reviewer changes it; *Accepted* means the decision was settled by the
project owner.

| ADR | Spec open question | Decision | Status |
| --- | --- | --- | --- |
| [0001](0001-payment-to-invoice-linking.md) | How is a payment linked to an invoice? | `PaymentRouter` contract: `pay(invoiceId, amount)` → `PaymentReceived` event | Accepted |
| [0002](0002-network-and-token.md) | Network and token | Base Sepolia + Circle test USDC (6 decimals); Anvil + MockUSDC locally | Accepted |
| [0003](0003-chain-reading.md) | How the service learns about payments | Own indexer: Nethereum `eth_getLogs` polling, persisted block cursor | Proposed |
| [0004](0004-confirmations.md) | How many confirmations, "confirming" status? | Configurable N (default 12 on Base Sepolia), explicit `Confirming` status | Proposed |
| [0005](0005-inexact-payment-rules.md) | Rules for inexact payments | Absolute tolerance (default 0.01 USDC), partial top-ups until deadline, `Underpaid`/`PaidAfterExpiry` terminal-for-review statuses | Proposed |
| [0006](0006-storage-and-background-processing.md) | Storage and background processing | PostgreSQL + EF Core, transactional outbox, `BackgroundService` workers | Proposed |
| [0007](0007-payment-page.md) | Payment page | Blazor page in the service, EIP-1193 browser-wallet connect + manual instructions | Proposed |
| [0008](0008-demo-deployment.md) | Demo deployment | Public instance on Base Sepolia **and** one-command local stack | Accepted (host: open) |
| [0009](0009-split-of-work.md) | Split of work | Author owns decisions + review; agent implements step by step via PRs | Proposed |
| [0010](0010-webhook-signing-and-delivery.md) | (FR-12..16, not an open question) | Stripe-style HMAC-SHA256 signature, outbox-driven retries | Proposed |
| [0011](0011-money-representation.md) | (NFR-1, not an open question) | Integer base units (`BigInteger` / `numeric(78,0)`), decimal strings in the API | Proposed |

The invoice status model and transition table that several ADRs depend on is in
[../INVOICE-STATUS-MODEL.md](../INVOICE-STATUS-MODEL.md).
