# Upwork portfolio card — draft

## Title (≤ 70 characters)

**USDC payments for ASP.NET Core: payment service + .NET SDK**

Alternatives:
- *Crypto payment gateway (USDC) for .NET — invoices, on-chain tracking, webhooks*
- *Non-custodial USDC payment gateway in C# / .NET 10*

## Role

[Your role, e.g. "Solo developer — architecture, backend, SDK, tests"]

## Description (problem → solution → result, 3–4 lines)

**Problem:** .NET shops and SaaS apps that want to accept stablecoins have to learn blockchain details — matching
transfers to orders, confirmations, chain reorganizations, lost or forged notifications.
**Solution:** AcceptCrypto.NET, a non-custodial USDC payment service plus a NuGet SDK (the Stripe + Stripe.net model):
a unique deposit address per invoice from the merchant's xpub (no private keys on the server), its own chain
scanner with confirmations and reorg rollback, and HMAC-signed webhooks with retries via a PostgreSQL outbox.
**Result:** the full cycle "invoice → payment → webhook" runs with one `docker compose up` command; scenarios such as
underpayment, late payment, a forced reorg, a merchant outage and forged webhooks are each covered by an automated
test, and the merchant app integrates with a few lines of C#. Testnet/local demo — no real money.

(≈ 600 characters. If Upwork's limit is tighter, drop the parenthesis in "Solution" and the last sentence of "Result".)

## Skills / tags

C# · .NET · ASP.NET Core · PostgreSQL · Entity Framework Core · REST API · Webhooks · Payment Gateway Integration ·
Blockchain · Ethereum · USDC / Stablecoin · Nethereum · Docker · xUnit · CI/CD (GitHub Actions)

(Pick the ones Upwork offers in its skill list; it allows a limited number.)

## Link

`https://github.com/alena-dev-soft/accept-crypto-net` [+ video link once recorded]

## What to screenshot (in this order; Upwork shows the first image as the cover)

1. **Cover — payment page "paid"** (`docs/images/payment-page-paid.png`) or, better for the cover, a single
   composed image: demo shop order → payment page *confirming* → *paid* → webhook log. The GIF
   `docs/images/payment-cycle.gif` already shows the cycle; for the cover use a still frame from it or from the
   testnet video.
2. **Payment page on Base Sepolia with QR** (`docs/images/base-sepolia-payment-page.png`, or a fresh one from your
   testnet run) — shows the real testnet + Circle USDC.
3. **MetaMask confirm dialog** next to the payment page (from the testnet run) — shows "pay from an ordinary wallet".
4. **Demo shop webhook log** (`docs/images/demo-shop-webhook-log.png`) — signed webhooks received, forged ones
   rejected.
5. **Architecture diagram** — render the Mermaid diagram from the README "Architecture" section (GitHub shows it;
   take a screenshot of the rendered diagram).
6. **"How the hard parts are solved" table** from the README — screenshot of the rendered table (problem → solution
   → proving test). Shows engineering depth to a client who doesn't read code.
7. Optional: **green CI run / test report** (GitHub Actions summary) once the workflow is active.

Rules for the images: no secrets, RPC URLs with keys, mnemonics or MetaMask phrases in any frame; browser zoom
~125 %; crop browser chrome.

## Open points for the author

- [ ] Role line, and whether to mention collaborators / AI-assisted parts (the spec lists "split of work" as an open question).
- [ ] Video link (after the testnet recording).
- [ ] Final cover image.
