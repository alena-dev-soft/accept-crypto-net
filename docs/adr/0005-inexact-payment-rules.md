# ADR 0005 — Rules for inexact and late payments

- **Status:** Proposed (default — reviewer please confirm the tolerance and the late-payment rule)
- **Date:** 2026-11-15
- **Spec:** FR-3, FR-11, scenarios 3 and 4

## Context

The router transfers exactly the amount the payer passes to `pay`, so there are no fee
deductions; inexact totals come from manual input, several partial payments, or payments after
the deadline. Funds always land in the merchant's wallet (non-custodial), so the service cannot
"reject" money — it can only classify it and tell the merchant. Refunds are out of MVP scope.

## Decision

Notation: `A` = invoice amount, `T` = tolerance, `C` = sum of **confirmed** payments whose block
timestamp ≤ `ExpiresAt`, `L` = sum of confirmed payments with block timestamp > `ExpiresAt`.

1. **Tolerance.** One absolute tolerance `Invoices:AmountTolerance`, default **0.01 USDC**
   (10 000 base units), applied symmetrically. `0` = exact match. The value used is copied onto
   the invoice at creation, so changing config never re-classifies old invoices.
2. **Paid** when `A − T ≤ C ≤ A + T`. **Overpaid** when `C > A + T`. Overpaid counts as paid for
   fulfilment; the excess (`C − A`) is reported in the webhook and API for a manual refund.
3. **Partial payments.** Every `PaymentReceived` log is a separate payment. While `0 < C < A − T`
   and the invoice is open, the status is `PartiallyPaid`; the payer can top up with more
   payments until the deadline. The payment page shows the remaining amount `A − C`.
4. **Deadline is block time, not wall-clock time.** A payment is on time if its *block
   timestamp* ≤ `ExpiresAt`, even if it gets its N-th confirmation later. A payment mined before
   the deadline is never "late" because of indexer lag.
5. **Expiry** happens only once the indexer's confirmed cursor has passed the deadline (the block
   at the cursor has timestamp > `ExpiresAt`) **and** no on-time `Detected` payment is still
   waiting for confirmations. Then: `Pending` → `Expired`; `PartiallyPaid` → `Underpaid`
   (terminal for automation; merchant decides: accept, ask for the rest off-band, or refund).
6. **Payment after expiry.** A confirmed late payment on an `Expired` or `Underpaid` invoice moves
   it to `PaidAfterExpiry`, whatever the amount. The service never auto-fulfils late money: the
   rate/stock may have changed, so the merchant decides. Payload includes `C`, `L` and `A`.
7. **Extra money on a closed invoice.** A confirmed payment on `Paid` that pushes the total above
   `A + T` → `Overpaid`. Further payments on `Overpaid`/`PaidAfterExpiry` do not change the
   status but always emit `invoice.payment_received`, so the merchant never misses money.
8. **Unknown invoice ID / wrong token / wrong contract** → stored as an unmatched payment, logged
   and counted in a metric; never attached to an invoice.

## Consequences

- Every case of FR-11 maps to an explicit status and an explicit webhook (see the transition
  table in [INVOICE-STATUS-MODEL.md](../INVOICE-STATUS-MODEL.md)).
- The status is a pure function of (amount, tolerance, deadline, confirmed/detected payments,
  cursor time) plus the current status — easy to unit-test exhaustively.
- Alternatives considered: percentage tolerance (surprising for big invoices), auto-accepting
  late full payments (risky for merchants with time-limited offers). Either can be added as an
  option later without changing the model.
