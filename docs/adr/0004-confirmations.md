# ADR 0004 — Configurable confirmations and an explicit `Confirming` status

- **Status:** Proposed (default)
- **Date:** 2026-11-15
- **Spec:** FR-7, FR-10, FR-12

## Context

A payment is final only after N confirmations (FR-7). Between "seen in a block" and "final" the
payer wants feedback ("we see your payment"), and the merchant may want to show "processing".

## Decision

- `Chain:Confirmations` (N) is configuration. A log in block `b` has `head − b + 1` confirmations
  and is final when that is ≥ N.
- Defaults: **Base Sepolia N = 12** (~24 s at 2 s blocks — far above observed L2 sequencer reorg
  depth, still short enough for a live demo); **Local/Anvil N = 2** (tests mine blocks explicitly).
- Status **`Confirming`** is shown while an open invoice has at least one `Detected`
  (not yet final) payment and its confirmed total has not reached the paid threshold.
  It is a real status: it produces an `invoice.confirming` webhook, and it is reversible
  (reorg → back to `Pending`/`PartiallyPaid`, with a webhook).
- `Paid`, `Overpaid`, `Underpaid`, `PaidAfterExpiry` are only ever reached from **confirmed**
  payments. Merchants must fulfil orders on `invoice.paid`/`invoice.overpaid`, never on
  `invoice.confirming` — the SDK README states this.
- The payment page shows the live confirmation count (`k / N`).

## Consequences

- One extra status and two extra transitions in the model, documented in
  [INVOICE-STATUS-MODEL.md](../INVOICE-STATUS-MODEL.md).
- Merchants that don't care about `Confirming` can ignore that event type.
- A deployment on a chain with different finality only changes configuration.
