# Demo: the whole stack in one command

Everything runs locally: PostgreSQL, a local chain (Anvil) with a **test-only** mock USDC, the AcceptCrypto.NET
service and the demo shop built on the SDK. No wallet, faucet or gas needed — a scripted payer sends the USDC.

```bash
git clone https://github.com/alena-dev-soft/accept-crypto-net && cd accept-crypto-net
docker compose up --build          # first build ≈ 2–4 min; add -d to get the terminal back
```

| What | URL |
| --- | --- |
| Demo shop | <http://localhost:5080> — catalog, orders, webhook log |
| Payment pages, API, API docs | <http://localhost:5052> — `/pay/{token}`, `/v1/*`, `/scalar`, `/health/ready` |
| Local chain (Anvil, chain 31337, a block every 2 s) | `http://localhost:8545` |
| PostgreSQL | `localhost:5432`, db `acceptcrypto`, user/password `postgres` |

What `up` starts (see [docker-compose.yml](../docker-compose.yml)):

1. `postgres`, `anvil` (waits until healthy);
2. `deploy` — one-off: deploys the mock USDC at `0x5FbDB2315678afecb367f032d93F642f64180aa3` and mints 1,000,000 USDC
   to the payer (Anvil account #1), then exits;
3. `service` — migrates the database, scans the chain, sends webhooks to `http://demoshop:8080/webhooks/acceptcrypto`
   (`demoshop` is on the SSRF allowlist; short demo retry schedule: 5 s → 30 s, 30 min window; 5 confirmations ≈ 10 s);
4. `demoshop` — the sample merchant app (orders + webhook log in the `shop-data` volume).

> ⚠ The API key, webhook secret, xpub and payer key in `docker-compose.yml` are **public test values** (the xpub
> belongs to the well-known `test test … junk` mnemonic). Never use them for anything real.

Check it all at once (what CI runs): `scripts/smoke-test.sh` → buys, pays, waits for `paid` on the payment page and
in the shop, sends a forged webhook. Reset everything: `docker compose down -v`.

## S1 → S2: buy, pay, see it paid

1. Open <http://localhost:5080>, click **Buy with USDC**. The shop creates an invoice through the SDK
   (`CreateInvoiceAsync`, idempotency key `order-<id>`) and redirects you to the payment page.
2. Pay it — copy the payment page URL from the browser:

   ```bash
   docker compose run --rm payer http://localhost:5052/pay/<token>
   # or: scripts/pay.sh http://localhost:5052/pay/<token>
   ```

3. The payment page switches to **Payment detected — confirming**, then **paid** after 5 blocks (~10 s).
4. The shop's order (Orders → the order) is **paid**; the webhook log shows `invoice.confirming` and `invoice.paid`
   as *processed*. The order page also shows the service's own view via `GetInvoiceAsync`: transfers, confirmations,
   webhook delivery attempts.

## More scenarios

All payer commands run as `docker compose run --rm payer …` (or `scripts/pay.sh …`).

| Scenario | How |
| --- | --- |
| **S3 underpaid / top-up** | `payer <url> --amount 1` → `underpaid`; top up the rest with `payer <url> --amount <remaining>` → `paid` (without `--amount` the payer sends the full invoice amount → `overpaid`) |
| **S3 overpaid** | `payer <url> --amount 100` → `overpaid` |
| **S3 in two parts** | `payer <url> --split 2` → `confirming` → `paid` |
| **S4 expired** | choose *expires in 2 min* in the shop, wait → `expired` |
| **S4 paid late** | after it expired: `payer <url>` → `paid_late` |
| **S5 shop down** | `docker compose stop demoshop`; pay an order; watch `service` logs retry (5 s, 10 s, 20 s, 30 s …); `docker compose start demoshop` → the queued webhooks arrive, the order becomes paid |
| **FR-8/FR-9 service down** | pay, then at once `docker compose stop service` (or stop it first and pay with `payer --to <address> --amount <amount>`); wait; `docker compose start service` → it scans the missed blocks from its checkpoint, the invoice becomes paid, the transfer is counted once |
| **FR-10 reorg** | pay, then within ~10 s: `docker compose run --rm payer reorg 4` → the payment disappears; the shop gets `payment.reverted` and `invoice.pending`; the invoice never becomes paid. One line: `scripts/pay.sh <url> && scripts/pay.sh reorg 4` |
| **FR-26 forged / replayed webhook** | buttons on <http://localhost:5080/webhooks>, or `scripts/webhook-attack.sh forged` (also `tampered`, `replay`, `stale`) — see below |
| **S7 unmatched transfer** | send money to the deposit address of an invoice that is already closed (e.g. a paid order; the address is on the shop's order page): `payer --to <address> --amount 1` → listed by `curl -H 'X-Api-Key: dev_api_key_00000000000000000000000000000' http://localhost:5052/v1/unmatched-payments` (reason `invoice_closed`) |
| Mine blocks / move chain time | `payer mine 5`, `payer warp 3600` |
| Balance of the payer | `payer balance` |

Note: the scanner asks the node only for transfers to **issued deposit addresses** (`eth_getLogs` filtered by
recipient, DECISIONS D14), so a transfer to a random address is never seen; late money to a closed invoice's address is.

### FR-26: forged and replayed webhooks

The webhook log page has four buttons (and `scripts/webhook-attack.sh <kind>` does the same from a terminal). The shop
sends the request to its own SDK endpoint `POST /webhooks/acceptcrypto`, like an attacker on the network would:

| Kind | Request | Shop answers |
| --- | --- | --- |
| `forged` | made-up `invoice.paid`, signed with a guessed secret | **400** `SignatureMismatch` |
| `tampered` | the last genuine webhook with the status changed to paid, original signature | **400** `SignatureMismatch` |
| `replay` | the last genuine webhook resent byte for byte | **200 duplicate** (event ID already processed, not applied) within 5 min; **400** `TimestampOutsideTolerance` after |
| `stale` | a genuine webhook as if captured 10 minutes ago, replayed now | **400** `TimestampOutsideTolerance` |

Every one is listed in the webhook log with its outcome (`rejected` / `duplicate`) and a `demo: <kind>` label; no
order changes. By hand: `curl -i -X POST http://localhost:5080/webhooks/acceptcrypto -H "AcceptCrypto-Timestamp: $(date +%s)" -H 'AcceptCrypto-Signature: v1=00' -d '{}'` → 400.

## Without Docker

Needs the .NET 10 SDK, PostgreSQL on `localhost:5432` (user/password `postgres`, database `acceptcrypto`) and
[Foundry](https://getfoundry.sh) (`anvil`). Uses the same test values from the `appsettings.Development.json` files.

```bash
scripts/local-chain.sh                                         # terminal 1: Anvil + mock USDC + funded payer
Chain__Enabled=true Persistence__MigrateOnStartup=true \
  dotnet run --project src/AcceptCrypto.Service               # terminal 2: http://localhost:5052
dotnet run --project src/AcceptCrypto.DemoShop                 # terminal 3: http://localhost:5080
PAY_USE_DOTNET=1 scripts/pay.sh http://localhost:5052/pay/<token>   # terminal 4
dotnet run --project src/AcceptCrypto.Payer -- mine 2          # local-chain.sh mines only on transactions: add confirmations
```

Or start Anvil with blocks every 2 s: `anvil --block-time 2`, then `dotnet run --project src/AcceptCrypto.Payer -- setup`.
