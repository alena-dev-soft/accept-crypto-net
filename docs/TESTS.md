# Tests: scenario / requirement → test

Every scenario of the spec (S1–S7) and every Definition-of-done item is proven by an automated test. The
**end-to-end** column runs the whole system — the real service host (`Program.cs`: merchant API, payment page, chain
scanner, expiry worker, webhook dispatcher as background services) on PostgreSQL and a local Anvil chain, the
scripted payer, and a merchant app built on the SDK (`AddAcceptCrypto` + `MapAcceptCryptoWebhook`) that receives the
signed webhooks over real HTTP. The **focused** column lists the tests that pin down the details (edge cases,
concurrency, exact event sequences) one layer lower.

Paths are relative to `tests/`. `ScenarioTests` = `AcceptCrypto.IntegrationTests/EndToEnd/ScenarioTests.cs`.

## Scenarios and Definition of done

| Scenario / requirement | End-to-end test | Focused tests |
| --- | --- | --- |
| **S1** Invoice created via the API, payment page opens from the link (FR-1, FR-4, FR-29) | `ScenarioTests.S1_invoice_created_via_the_api_and_the_payment_page_opens_from_the_link` | `IntegrationTests/Api/MerchantApiTests.Create_returns_201_with_payment_details_and_payment_page_link`; `IntegrationTests/Api/PaymentPageTests.Page_shows_the_payment_details_from_the_link_returned_by_create`, `…Page_qr_and_status_json_never_contain_the_order_id_metadata_or_invoice_id`; `Domain.Tests/InvoiceCreationTests` |
| **S2** Exact payment → `paid` → webhook received and verified by the SDK → page shows it (FR-6, FR-7, FR-12, FR-13, FR-30) | `ScenarioTests.S2_exact_payment_ends_paid_the_merchant_verifies_the_webhook_and_the_page_shows_paid` | `IntegrationTests/Scanner/ChainScannerTests.Exact_payment_goes_confirming_then_paid_after_N_confirmations`; `IntegrationTests/Scanner/ScannerHostingTests.Service_detects_and_confirms_a_payment_in_the_background`; `IntegrationTests/Api/SdkContractTests.A_webhook_sent_by_the_dispatcher_during_rotation_is_verified_and_parsed_by_the_sdk`; `IntegrationTests/Api/PaymentPageTests.Status_json_follows_the_invoice_through_confirming_underpaid_and_paid`; `DemoShop.Tests/DemoShopTests.A_signed_webhook_updates_the_order_and_a_duplicate_is_not_applied_twice` |
| **S3** Underpaid, overpaid, split payment (FR-11) | `ScenarioTests.S3_underpayment_overpayment_and_a_split_payment_end_underpaid_overpaid_and_paid` | `ChainScannerTests.Underpayment_is_reported_and_a_top_up_completes_the_invoice`, `…Overpayment_ends_overpaid_with_the_excess_visible`, `…Split_payment_in_two_transfers_sums_up_to_paid`; `Domain.Tests/InvoiceStateMachineTests` (every edge of the status diagram) |
| **S4** Expired; paid after expiry → `paid_late` (FR-3, FR-11) | `ScenarioTests.S4_unpaid_invoice_expires_and_a_payment_after_the_deadline_ends_paid_late` | `ChainScannerTests.Expiry_waits_for_the_scanner_and_a_late_payment_ends_paid_late`, `…Payment_on_time_but_scanned_after_the_deadline_still_counts_as_paid`; `IntegrationTests/Persistence/QueryAndCheckpointTests.Due_for_expiry_lists_only_open_invoices_past_their_deadline` |
| **S5** Merchant down → webhook delivered once it is back (FR-14) | `ScenarioTests.S5_merchant_down_when_the_webhook_is_sent_receives_it_once_it_is_back` | `IntegrationTests/Webhooks/WebhookDeliveryTests.S5_merchant_down_is_retried_with_backoff_and_delivered_once_it_is_back`, `…The_hosted_dispatcher_keeps_running_while_the_merchant_is_down_and_delivers_when_it_is_back`, `…Non_2xx_answers_are_retried_and_the_event_is_failed_after_the_limit_then_a_resend_keeps_the_event_id`, `…The_event_is_failed_when_the_retry_window_is_over_even_below_the_attempt_limit`; `Infrastructure.Tests/Webhooks/WebhookRetryPolicyTests` |
| **S6** Invoice list (filters, cursor) and payment + webhook history (FR-18, FR-19) | `ScenarioTests.S6_merchant_lists_invoices_by_status_and_sees_the_payment_and_webhook_history` | `MerchantApiTests.List_is_cursor_paginated_newest_first_without_gaps_or_duplicates`, `…List_filters_by_status_and_creation_date`, `…Get_shows_transfers_and_webhook_history` |
| **S7** Unmatched transfer listed (FR-11a) | `ScenarioTests.S7_transfers_that_match_no_open_invoice_are_listed_as_unmatched` | `ChainScannerTests.Transfer_to_a_paid_invoice_is_recorded_once_as_unmatched`; `IntegrationTests/Persistence/TransferDeduplicationTests.Transfer_to_a_closed_invoice_goes_to_unmatched`; `SdkContractTests.Unmatched_payments_are_listed_by_the_client`; `MerchantApiTests.Unmatched_payments_are_listed_newest_first_with_a_cursor` |
| **FR-8 / FR-9** Service stopped during a payment → found after restart, not counted twice; catch-up in chunks from the checkpoint | `ScenarioTests.FR8_FR9_service_stopped_during_a_payment_finds_it_after_restart_and_counts_it_once` | `ChainScannerTests.Restart_in_the_middle_of_a_payment_does_not_count_it_twice`, `…Payment_made_while_the_service_is_down_is_found_after_restart_in_chunks`; `TransferDeduplicationTests.Parallel_recording_of_the_same_transfer_stores_it_once`, `…The_database_rejects_a_second_row_with_the_same_key`; `QueryAndCheckpointTests.Scanner_checkpoint_is_upserted` |
| **FR-10** Forced reorg rolls back a `confirming` payment, never `paid`; `payment.reverted` sent | `ScenarioTests.FR10_forced_reorg_rolls_back_a_confirming_payment_and_never_produces_paid` | `ChainScannerTests.Forced_reorg_rolls_back_a_confirming_payment_and_never_produces_paid`, `…Reorg_of_part_of_a_split_payment_moves_the_invoice_back_to_underpaid`; `IntegrationTests/Chain/LocalChainTests.Forced_reorg_removes_the_transfer_and_replaces_the_block_hash` |
| **FR-26** Forged / tampered / replayed webhook rejected by the shop; duplicate detected (FR-16) | `ScenarioTests.FR26_forged_tampered_and_replayed_webhooks_are_rejected_by_the_merchant_app` | `Sdk.Tests/WebhookEndpointTests.AForgedWebhookIsRejectedWithoutCallingTheHandler`, `…AReplayedWebhookOutsideTheToleranceIsRejected`, `…UnsignedRequestsAreRejected`, `…ADuplicateDeliveryIsAcknowledgedButHandledOnce`; `Sdk.Tests/WebhookVerifierTests`; `DemoShopTests.Forged_tampered_and_stale_webhooks_are_rejected_and_shown_in_the_log` |
| One-command stack, full cycle (NFR-9, DoD) | CI job `compose-smoke` (`docker compose up` + `scripts/smoke-test.sh`: S1 → S2 → webhook → forged webhook) | — |

## Other requirements

| Requirement | Tests |
| --- | --- |
| FR-0 Merchant configuration, secret rotation | `IntegrationTests/Api/ConfigurationValidationTests` (all); `WebhookDeliveryTests.During_secret_rotation_the_service_signs_with_the_new_and_the_previous_secret`; `Sdk.Tests/WebhookVerifierTests.RotationReceiverWithNewAndOldSecretAcceptsAWebhookSignedOnlyWithTheOldSecret`, `…RotationServiceSendingTwoSignaturesIsAcceptedByAReceiverKnowingEitherSecret` |
| FR-1 Amount validation, min/max, 6 decimals | `MerchantApiTests.Invalid_create_requests_return_validation_problems`; `Domain.Tests/UsdcAmountTests`; `Domain.Tests/InvoiceCreationTests` |
| FR-2 Idempotent create | `MerchantApiTests.Same_idempotency_key_and_body_returns_the_original_invoice`, `…Same_idempotency_key_with_a_different_body_is_rejected`, `…Parallel_requests_with_one_idempotency_key_create_one_invoice`; `IntegrationTests/Persistence/IdempotentCreationTests` |
| FR-5 Unique deposit address per invoice | `IdempotentCreationTests.Requests_without_a_key_get_distinct_derivation_indexes_and_addresses`; `Infrastructure.Tests/Chain/XpubDepositAddressDeriverTests`; `ChainScannerTests.Transfers_to_addresses_that_are_not_deposit_addresses_are_ignored` |
| FR-13 HMAC-SHA256 over `{timestamp}.{body}`, headers | `Infrastructure.Tests/Webhooks/WebhookSignatureTests`; `WebhookDeliveryTests.Delivers_the_stored_payload_signed_with_timestamp_and_event_id_headers`; `Sdk.Tests/WebhookVerifierTests.ComputesTheDocumentedReferenceSignatures` |
| FR-15 Manual resend keeps the event ID | `MerchantApiTests.Resend_requeues_the_event_with_its_original_id_and_payload`; `OutboxAtomicityTests.Delivery_history_and_manual_resend_keep_the_event_id` |
| FR-17 API key on every merchant call | `MerchantApiTests.Every_merchant_endpoint_requires_the_api_key` |
| FR-20 Problem Details | `MerchantApiTests.Malformed_bodies_return_400_problem_details`, `…Get_unknown_invoice_returns_404_problem`; `SdkContractTests.Service_errors_surface_as_typed_exceptions` |
| FR-21 OpenAPI | `MerchantApiTests.Health_and_openapi_are_public_and_the_document_describes_every_operation` |
| FR-22–FR-28 SDK | `Sdk.Tests/*` (client, options, events, endpoint, verifier); `SdkContractTests.The_typed_client_covers_every_merchant_operation_against_the_real_service` |
| FR-29 / FR-30 Payment page | `IntegrationTests/Api/PaymentPageTests` (all) |
| NFR-1 Money without precision loss | `Domain.Tests/UsdcAmountTests`; `Sdk.Tests/UsdcAmountAndDeduplicationTests` |
| NFR-2 Status change + event atomic | `OutboxAtomicityTests.Status_change_and_its_events_are_committed_together_in_causal_order`, `…If_the_outbox_insert_fails_the_status_change_is_rolled_back` |
| NFR-3 No status race | `IntegrationTests/Persistence/ConcurrencyTests` (all); `WebhookDeliveryTests.Parallel_dispatchers_deliver_every_event_exactly_once` |
| NFR-4 RPC / merchant unavailable | `ScannerHostingTests.Unreachable_rpc_is_retried_with_backoff_and_the_service_keeps_running`; `WebhookDeliveryTests.A_slow_merchant_times_out_and_is_retried`; `ScenarioTests.S5_…` |
| NFR-5 Secrets not in logs | `MerchantApiTests.Secrets_never_appear_in_logs` |
| NFR-6 Input validation, SSRF guard + allowlist | `Infrastructure.Tests/Webhooks/WebhookUrlGuardTests`; `WebhookDeliveryTests.A_webhook_url_on_the_internal_network_is_refused_without_a_request`; `ConfigurationValidationTests.Allowlisted_internal_webhook_hosts_are_accepted` |
| NFR-7 Metrics, health checks | `IntegrationTests/EndToEnd/ObservabilityTests.Metrics_endpoint_reports_chain_lag_webhook_queue_and_delivery_outcomes`, `…Health_reports_database_rpc_scanner_and_webhook_queue`; `WebhookDeliveryTests.Queue_metrics_report_pending_due_and_failed_events`; `IntegrationTests/HealthEndpointTests` |

## How to run

```bash
dotnet test AcceptCrypto.NET.slnx                                              # everything (needs Docker or the variables below)
dotnet test tests/AcceptCrypto.IntegrationTests --filter "FullyQualifiedName~EndToEnd"   # only the end-to-end scenarios (~45 s)
```

- **PostgreSQL**: a Testcontainers container (`postgres:17-alpine`), or an existing server from
  `ACCEPTCRYPTO_TEST_POSTGRES` (connection string with rights to create databases; every test gets its own database).
- **Anvil**: `ACCEPTCRYPTO_TEST_ANVIL_RPC` (running Anvil, it is reset), else an `anvil` binary (PATH, `~/.foundry/bin`,
  `ACCEPTCRYPTO_TEST_ANVIL_BIN`), else a Testcontainers container from `ghcr.io/foundry-rs/foundry`.
- The chain tests share one Anvil and run sequentially (xUnit collection `anvil`); the rest runs in parallel.

**Stability measures** (no sleeps-and-hope): every wait polls the API or the merchant app's received events with a
deadline and reports the last state seen on timeout (`EndToEnd/EndToEndHarness.cs`, `Eventually`); every test
gets a fresh database and a reset chain whose clock is set to the wall clock (deadlines are compared with block
timestamps); the merchant app listens on a fixed port so it can go down and come back; CI uses
`--blame-hang-timeout 5m` so a hang fails fast with a dump.

In CI (`docs/ci/ci.yml`) every run publishes a test report (check run + job summary from the `.trx` files), a coverage
summary, and the raw results as an artifact.
