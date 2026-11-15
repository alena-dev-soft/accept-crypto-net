# ADR 0008 — Public demo instance + one-command local stack

- **Status:** Accepted (public instance settled by the project owner; hosting provider is open)
- **Date:** 2026-11-15
- **Spec:** NFR-9, Definition of done, "Packaging for Upwork"

## Context

A client spends ~30 seconds on the README; a link to a live demo is much stronger than
"clone and run". The Definition of done also requires that a stranger can start the stack
locally within 10 minutes.

## Decision

- **Public instance** on Base Sepolia: service (API + payment page) + demo shop + PostgreSQL,
  shipped as container images built by CI. The demo shop's "Buy" button creates a real testnet
  invoice; visitors pay with their own MetaMask and Circle-faucet USDC.
- **Local stack**: `docker compose up` starts PostgreSQL, Anvil (with MockUSDC + PaymentRouter
  deployed by a Foundry script), the service and the demo shop (NFR-9). A helper script/command
  mints MockUSDC to an Anvil account so the cycle works fully offline.
- Hardening for a public instance: API key per environment (from secrets), rate limiting on
  the public endpoints, testnet banner on every page, invoice amount cap, data retention job.
- **Open point for the reviewer:** which host (e.g. Azure Container Apps, Fly.io, Render, a VPS)
  and which domain. It does not affect code: configuration comes from environment variables.

## Consequences

- CI publishes images; deployment is a separate step later in the plan.
- The public RPC provider key and the API key live in the host's secret store, never in git.
