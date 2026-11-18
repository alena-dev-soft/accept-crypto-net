# Upwork Project Catalog offer — draft

> Everything in `[SQUARE BRACKETS]` is the author's decision (prices, delivery times, revisions, scope limits).
> Nothing here states the author's experience, clients or results — add only what is true.

## Title

**Add crypto (USDC) payments to your .NET app**

Alternatives (Upwork titles start with "You will get…" in some layouts):
- *You will get USDC payments integrated into your ASP.NET Core app*
- *You will get a stablecoin payment flow for your .NET backend: invoices, on-chain tracking, webhooks*

## Category

[Development & IT → Web Development / Back-End Development / Blockchain — choose the closest]

## Description

Want to accept USDC in your ASP.NET Core application without becoming a blockchain expert? I'll add a
non-custodial stablecoin payment flow to your .NET backend: invoices with a payment page for your customers,
automatic detection of on-chain payments, and reliable, signed notifications to your app when an order is paid.

The approach is the one from my open-source project AcceptCrypto.NET [author: adjust wording to how the project was built]
([GitHub link]) — a payment service plus a .NET SDK, with a test for every scenario:

- **Non-custodial:** each invoice gets its own deposit address derived from your wallet's public key (xpub). No
  private keys on the server; the funds go straight to addresses of your wallet.
- **Correct money handling:** amounts as exact integers (no floating point), idempotent invoice creation, no payment
  counted twice — even after restarts.
- **Real-world edge cases:** confirmations before "paid", chain reorganizations rolled back, underpayments,
  overpayments, split payments, late payments, transfers that match no order.
- **Webhooks you can trust:** HMAC-signed, retried with backoff until your app is back, duplicate-safe, with secret
  rotation.
- **Clean .NET:** .NET 10 / ASP.NET Core, PostgreSQL, background workers, OpenAPI, Problem Details errors,
  structured logs, health checks and metrics, automated tests, Docker.

You get working code in your repository, tests, and documentation your team can maintain.

[Optional: one sentence about the author's background — only verifiable facts.]

## What's included (all packages)

- Analysis of your current checkout / order flow and a short integration plan
- Invoice creation from your app (idempotent) and a payment page for your customers (address, exact amount, QR,
  live status)
- On-chain payment detection for USDC on [the agreed EVM network(s)] with configurable confirmations
- Order status updates in your app via signed webhooks
- Configuration via environment variables / secrets (no keys in code)
- Testnet demo of the full cycle (invoice → payment → webhook)
- Documentation: how to run, configure and test it

**Not included unless agreed separately** (state clearly in the offer): mainnet go-live and its operational
responsibility, custody or moving of funds (sweeping), refunds/payouts, fiat conversion, KYC/AML, smart-contract
audits. [Author: decide which of these you offer at all; the open-source project itself is testnet-only.]

## Packages

| | Starter | Standard | Advanced |
| --- | --- | --- | --- |
| Name | [e.g. "Testnet proof of concept"] | [e.g. "Integration into your app"] | [e.g. "Integration + operations"] |
| Summary | AcceptCrypto.NET-style service running against a testnet, connected to a sample or a staging copy of your app | Payment flow integrated into your ASP.NET Core app (checkout → payment page → webhook → order paid) | Standard + [the extras you choose, e.g. dashboard views of invoices/unmatched payments, alerting on metrics, deployment to your infrastructure] |
| Networks / tokens | 1 testnet, USDC | [1 network, USDC] | [up to N networks / tokens] |
| Edge cases (under/over/late/unmatched) | shown in the demo | handled in your order logic | handled + reconciliation workflow [describe] |
| Webhook handling in your app | sample handler | integrated, with dedup | integrated, with dedup + [monitoring] |
| Tests | scenario tests of the service | + tests of your integration | + [load / failure tests?] |
| Deployment | Docker Compose | [your environment: …] | [your environment + CI/CD] |
| Documentation | README | README + runbook | README + runbook + [handover call] |
| Revisions | [N] | [N] | [N] |
| Delivery time | [DAYS] | [DAYS] | [DAYS] |
| **Price** | **[PRICE]** | **[PRICE]** | **[PRICE]** |

## Optional add-ons (Upwork "extras")

- [Additional network or token — PRICE / DAYS]
- [Payment page in your brand/design — PRICE / DAYS]
- [Fast delivery — PRICE]
- [Extra revision / support hours — PRICE]

## Requirements from the buyer

1. Access to the repository of your .NET application (or a description of the order/checkout flow).
2. Target network and token ([e.g. Base, Ethereum, Polygon] / USDC) and how many confirmations you consider final.
3. An **xpub of a wallet you control** for the receiving addresses (never a seed phrase or private key — I don't
   need and won't accept them).
4. Your rules for inexact payments: tolerance, partial payments, payments after the deadline, overpayments.
5. Where it should run (your server/cloud, Docker available?) and an RPC provider account (or permission to create one).

## FAQ (draft answers — check every statement)

**Do you hold or touch my funds?**
No. Customers pay to addresses derived from your wallet's public key; only you have the keys to move the funds.

**Is this production-ready?**
[Author's answer. Facts: the open-source project is a testnet/local portfolio project, explicitly "not for
production"; it demonstrates the patterns. Decide what you guarantee for client work.]

**Which networks are supported?**
The project is built for EVM chains with a 6-decimal USDC token (tested on a local chain and Base Sepolia).
[Other networks — your decision / add-on.]

**What if my server is down when a payment arrives?**
The service catches up from a saved checkpoint and retries webhooks with backoff until your app answers; no payment
or notification is lost (both behaviours are covered by tests in the project).

**Can I see it working first?**
Yes: the GitHub repository starts the whole cycle with one command, and [video link] shows a payment from MetaMask
on a public testnet.

## Gallery (Upwork allows images/video)

1. [Video: the 1–2 minute testnet recording — see docs/testnet.md]
2. Payment page with QR (`docs/images/base-sepolia-payment-page.png` or from the testnet run)
3. Demo shop: order paid + webhook log (`docs/images/demo-shop-order-paid.png`, `demo-shop-webhook-log.png`)
4. Architecture diagram (rendered from the README)
