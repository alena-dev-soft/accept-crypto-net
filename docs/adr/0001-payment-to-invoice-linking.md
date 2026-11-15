# ADR 0001 — Linking payments to invoices through a PaymentRouter contract

- **Status:** Accepted (settled by the project owner)
- **Date:** 2026-11-15
- **Spec:** FR-4, FR-5, FR-8, NFR-5

## Context

A plain ERC-20 `transfer` carries no reference. With many open invoices the service must still
match every payment to exactly one invoice (FR-5), without holding private keys or funds (NFR-5).
Common options:

| Option | Problem |
| --- | --- |
| Unique amount per invoice ("12.000137 USDC") | Collisions, payer must type odd amounts, breaks on partial payments |
| Deposit address per invoice (HD wallet) | Service must hold keys and sweep funds — custodial, against NFR-5 |
| Memo / calldata on transfer | ERC-20 `transfer` has no memo field |
| **Router contract that carries the invoice ID** | One small contract to write, audit and deploy |

## Decision

A minimal, ownerless `PaymentRouter` contract (`contracts/src/PaymentRouter.sol`):

```solidity
constructor(IERC20 token, address merchant)            // both immutable
function pay(bytes32 invoiceId, uint256 amount) external
event PaymentReceived(bytes32 indexed invoiceId, address indexed payer, address indexed token, uint256 amount);
```

- `pay` calls `token.transferFrom(msg.sender, merchant, amount)` — USDC goes **straight from the
  payer to the merchant**; the router never holds a balance. The payer first `approve`s the
  router for the amount (two wallet confirmations; an EIP-2612 `permit` variant is a later option).
- `amount == 0` and `invoiceId == 0` revert. `SafeERC20`-style return-value checks are used.
- The contract does **not** know invoices, amounts or deadlines (the service holds no key to
  register them on-chain). Every rule about amount and time is applied off-chain by the service
  (see [0005](0005-inexact-payment-rules.md)).
- `sweep()` (permissionless) forwards any token balance accidentally sent to the router with a
  plain `transfer` to the immutable merchant address. It cannot send funds anywhere else.
- **Invoice ID on-chain:** `bytes32` = the invoice's `Guid` (16 bytes, RFC 4122 byte order,
  big-endian) left-padded with zeros. The mapping is a pure function, so no lookup table is needed.
- The service only trusts logs **emitted by the configured router address**; the `token` topic
  must equal the configured USDC address. A log for an unknown invoice ID is stored as an
  *unmatched payment* (visible in logs/metrics) and never attached to an invoice.
- **Payment identity:** `(chainId, txHash, logIndex)` — unique index in the database (FR-8).

## Consequences

- FR-5 is solved by construction: the invoice ID is in the event, no heuristics.
- Non-custodial: the service has read-only RPC access and no keys at all.
- The payer must use a wallet that can call a contract (browser wallet via EIP-1193). A plain
  "send USDC to this address" QR code is **not** sufficient: such a transfer cannot be attributed
  (the payment page says so explicitly, see [0007](0007-payment-page.md)).
- Anyone can call `pay` with any invoice ID and any amount — this is fine: the funds still go to
  the merchant, and the service validates amounts/time off-chain.
- One router per (merchant, token, chain). Multi-merchant would need a merchant parameter or a
  router per merchant — out of MVP scope.
