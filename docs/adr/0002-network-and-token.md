# ADR 0002 — Network and token: Base Sepolia + Circle test USDC; Anvil + MockUSDC locally

- **Status:** Accepted (network/token settled by the project owner; local profile proposed)
- **Date:** 2026-11-15
- **Spec:** MVP scope "one token, one testnet", NFR-8, NFR-9

## Context

The MVP supports exactly one network and one token on a testnet. The demo must be reproducible
by a stranger in 10 minutes, and integration tests need a chain that is fast, free and offline.

## Decision

**Public profile (`BaseSepolia`)**

| Setting | Value |
| --- | --- |
| Chain | Base Sepolia, chain ID `84532` |
| Token | Circle test USDC, 6 decimals (address from Circle's docs, kept in configuration, not code) |
| Test funds | Circle faucet for USDC, a Base Sepolia faucet for ETH gas |
| RPC | Any Base Sepolia JSON-RPC endpoint; URL from configuration/secrets (provider keys are secrets) |
| Router | `PaymentRouter` deployed once with Foundry (`forge script`), address in configuration |

**Local profile (`Local`)**

- [Anvil](https://book.getfoundry.sh/anvil/) (Foundry) as the chain, chain ID `31337`.
- `MockUSDC` (ERC-20, 6 decimals, public `mint`) + `PaymentRouter` deployed by a Foundry script.
- Used by integration tests (NFR-8), offline demos and the one-command stack (NFR-9).
- Anvil can mine on demand and simulate reorgs (`anvil_mine`, `evm_snapshot`/`evm_revert`),
  which makes the confirmation and reorg tests deterministic.

Token, decimals, router address, chain ID, RPC URL and confirmation count are all configuration
(`Chain:*` options). No address is hard-coded in C#.

## Consequences

- Real, public chain for the portfolio demo (MetaMask, block explorer links) without real money.
- The domain code never assumes 6 decimals: decimals come from configuration and are validated
  against the token's `decimals()` at startup.
- Base Sepolia's ~2 s block time keeps confirmation waits short in demos.
- The public demo depends on a third-party RPC provider and faucets; NFR-4 covers RPC outages.
