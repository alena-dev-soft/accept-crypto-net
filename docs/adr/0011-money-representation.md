# ADR 0011 — Money as integer base units

- **Status:** Proposed (default)
- **Date:** 2026-11-15
- **Spec:** NFR-1

## Decision

- In the domain, an amount is a `TokenAmount` value object: `BigInteger BaseUnits` + `byte
  Decimals` (+ token symbol). Arithmetic and comparison only between equal tokens/decimals;
  no `double` anywhere, no `decimal` in the core (on-chain values are `uint256`).
- PostgreSQL column type `numeric(78,0)` (fits any `uint256`), mapped through an EF Core value
  converter.
- The public API and webhooks use **decimal strings** (`"25.50"`), parsed strictly: no exponent,
  no more fractional digits than the token's decimals (`"0.0000001"` USDC → 400 Problem
  Details), must be > 0 and ≤ the configured maximum. The SDK exposes amounts as `decimal`
  (USDC fits easily) and serialises them as strings.
- On-chain amounts are taken from the event's `uint256` without conversion.

## Consequences

Exact comparisons for the tolerance rules ([0005](0005-inexact-payment-rules.md)); no rounding
bugs; the same code works for 18-decimal tokens later.
