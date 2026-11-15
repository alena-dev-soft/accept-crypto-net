# ADR 0007 — Blazor payment page with EIP-1193 browser-wallet connect

- **Status:** Proposed (default)
- **Date:** 2026-11-15
- **Spec:** FR-4, scenario 1, "Packaging for Upwork" (video: pay from MetaMask)

## Context

Because payments go through `PaymentRouter.pay` ([0001](0001-payment-to-invoice-linking.md)),
a plain "send USDC to this address" QR code cannot be attributed to an invoice. The payer needs
a wallet that can sign an `approve` and a `pay` call. The stack is .NET; a separate SPA
(React/Vue) would add a second toolchain.

## Decision

- The payment page lives in `AcceptCrypto.Service` at `/pay/{invoiceId}` as a **Blazor**
  (Blazor Web App, .NET 8) page: static SSR for the invoice details (works without JS), an
  interactive component for wallet actions and live status.
- Wallet integration through a small JS module calling the **EIP-1193** provider
  (`window.ethereum`: MetaMask, Coinbase Wallet, Rabby …) via `IJSRuntime`:
  connect → switch/add chain (EIP-3085/3326) → `approve(router, amount)` if allowance is
  insufficient → `pay(invoiceId, amount)`. Calldata is built server-side (Nethereum ABI
  encoding) so the JS stays tiny and has no web3 library dependency.
- The page shows FR-4 details: network, token + address, router address, invoice ID (`bytes32`),
  exact remaining amount, deadline countdown, current status and confirmations (`k / N`), and a
  block-explorer link for each detected transaction.
- No-wallet fallback: manual instructions (contract address + function + arguments, i.e. what
  to enter in a block explorer's "Write contract" tab) and a clear warning that a plain token
  transfer is not attributable.
- Status updates by polling the public invoice status endpoint every few seconds (simple, works
  behind any proxy); SignalR push is a possible later improvement.

## Consequences

- One language and one deployment for API + page; the page doubles as the demo video set.
- Mobile wallets without an injected provider need the manual path; WalletConnect is roadmap.
- The public page exposes only payer-facing data, never merchant metadata or webhook info.
