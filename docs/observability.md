# Observability (NFR-7)

Three things answer "is the service doing its job?": structured logs, health checks and metrics.
Decisions: DECISIONS.md **D20** (and D14/D16 for the instruments themselves).

## Logs

- Production: JSON console logs (built-in `Microsoft.Extensions.Logging` JSON formatter, UTC timestamps, scopes —
  `appsettings.json` → `Logging:Console`). Development: the simple single-line formatter.
- Every log event of the service is a source-generated `LoggerMessage` with named properties (invoice ID, status,
  tx hash, block numbers, attempt number…) — never metadata, API keys, secrets or the xpub (test:
  `MerchantApiTests.Secrets_never_appear_in_logs`). The startup line summarises the configuration (redacted).

```json
{"Timestamp":"2026-11-18T04:56:13.017Z","EventId":1929458206,"LogLevel":"Information","Category":"AcceptCrypto.Infrastructure.Chain.Scanning.ChainScannerService","Message":"Chain scanner started (chain 31337, 2 confirmations)","State":{"ChainId":31337,"Confirmations":2,"{OriginalFormat}":"Chain scanner started (chain {ChainId}, {Confirmations} confirmations)"},"Scopes":[]}
```

## Health checks

| Endpoint | Checks | Use |
| --- | --- | --- |
| `GET /health` | `chain-scanner` (Degraded when no scan pass succeeded for `Chain:StaleAfter`), `webhooks` | Liveness; plain text `Healthy` / `Degraded` / `Unhealthy` |
| `GET /health/ready` | the above + `database`, `rpc` | Readiness (docker compose waits on it); JSON with per-check data |

`webhooks` is Degraded when events failed after all retries (resend them via `POST /v1/events/{id}/resend`) or the
oldest pending event is older than 15 minutes (merchant unreachable). It is never Unhealthy: a merchant outage must
not get the service restarted.

```json
{"status":"Healthy","checks":{"chain-scanner":{"status":"Healthy","data":{"head_block":2587,"last_scanned_block":2587,"lag_blocks":0,"consecutive_failures":0}},
 "database":{"status":"Healthy"},"webhooks":{"status":"Healthy","data":{"pending":0,"due":0,"failed":0,"oldest_pending_age_seconds":0}},
 "rpc":{"status":"Healthy","data":{"head_block":2587}}}}
```

## Metrics

`GET /metrics` — Prometheus text format via OpenTelemetry (`Observability:MetricsEnabled`, default `true`;
`Observability:MetricsPath`, default `/metrics`). Like `/health` it needs no API key: keep it on the internal network
or restrict it at the reverse proxy.

| Metric (Prometheus name) | Type | Meaning |
| --- | --- | --- |
| `acceptcrypto_scanner_lag_blocks` | gauge | Blocks between the chain head and the last scanned block — **lag behind the chain head** |
| `acceptcrypto_scanner_head_block` / `acceptcrypto_scanner_last_scanned_block` | gauge | Chain head seen / checkpoint |
| `acceptcrypto_scanner_transfers_total` | counter | New token transfers to deposit addresses |
| `acceptcrypto_scanner_reorgs_total` | counter | Reorganisations detected |
| `acceptcrypto_scanner_failed_passes_total` | counter | Scan passes that failed (RPC/DB errors) |
| `acceptcrypto_webhooks_queue_length` | gauge | Events waiting for delivery — **webhook queue** |
| `acceptcrypto_webhooks_due` | gauge | Pending events whose next attempt is due now |
| `acceptcrypto_webhooks_failed` | gauge | Events that exhausted their retries — **delivery failures** waiting for a resend |
| `acceptcrypto_webhooks_oldest_pending_age_seconds` | gauge | Age of the oldest pending event |
| `acceptcrypto_webhooks_attempts_total{outcome="delivered\|retry\|failed"}` | counter | Delivery attempts by outcome |
| `acceptcrypto_webhooks_attempt_duration_seconds` | histogram | Duration of delivery attempts |

Plus the built-in meters: `http_server_request_duration_seconds` (ASP.NET Core), Kestrel connections,
`http_client_request_duration_seconds` (RPC and webhook calls), .NET runtime (GC, thread pool, CPU).

Suggested alerts: `acceptcrypto_scanner_lag_blocks > 50` for 5 min (scanner stuck or RPC down);
`acceptcrypto_webhooks_failed > 0`; `acceptcrypto_webhooks_oldest_pending_age_seconds > 900`;
`rate(acceptcrypto_scanner_failed_passes_total[5m]) > 0`.

Proven by `tests/AcceptCrypto.IntegrationTests/EndToEnd/ObservabilityTests.cs` (merchant down → queue and retries
visible; back up → queue drained, delivered counted; health shows every check).
