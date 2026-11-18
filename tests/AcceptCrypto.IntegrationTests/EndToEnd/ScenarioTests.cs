using System.Globalization;
using System.Net;
using System.Text;
using AcceptCrypto.IntegrationTests.Chain;
using AcceptCrypto.IntegrationTests.Persistence;
using AcceptCrypto.LocalChain;
using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;

namespace AcceptCrypto.IntegrationTests.EndToEnd;

/// <summary>
/// End-to-end scenarios S1–S7 of the spec plus FR-8/FR-9, FR-10 and FR-26 against the whole system: the real service
/// host on PostgreSQL and Anvil, the scripted payer, and a merchant app on the SDK receiving signed webhooks over HTTP.
/// Nothing is driven by hand: the scanner, expiry worker and webhook dispatcher run as background services.
/// docs/TESTS.md maps every scenario to these tests.
/// </summary>
[Collection(AnvilTestGroup.Name)]
public sealed class ScenarioTests(AnvilFixture chain, PostgresFixture postgres)
{
    [Fact]
    public async Task S1_invoice_created_via_the_api_and_the_payment_page_opens_from_the_link()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);

        var invoice = await h.CreateInvoiceAsync("25.5", orderId: "order-s1");

        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Equal(UsdcAmount.Parse("25.5"), invoice.Payment.Amount);
        Assert.Equal("USDC", invoice.Payment.Token);
        Assert.Equal(6, invoice.Payment.Decimals);
        Assert.Equal(AnvilAccounts.ChainId, invoice.Payment.ChainId);
        Assert.Matches("^0x[0-9a-f]{40}$", invoice.Payment.Address);
        Assert.True(invoice.ExpiresAt > DateTimeOffset.UtcNow);

        // The link from the API opens the public payment page (no API key) with the FR-4 details only (FR-29).
        Assert.StartsWith("/pay/", invoice.PaymentPageUrl.AbsolutePath, StringComparison.Ordinal);
        using var page = await h.Http.GetAsync(new Uri(invoice.PaymentPageUrl.PathAndQuery, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains(invoice.Payment.Address, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("25.5", html, StringComparison.Ordinal);
        Assert.DoesNotContain("order-s1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("c-42", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S2_exact_payment_ends_paid_the_merchant_verifies_the_webhook_and_the_page_shows_paid()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var invoice = await h.CreateInvoiceAsync("10", orderId: "order-s2");

        var tx = await h.PayAsync(invoice, "10");
        await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.Confirming);
        var paid = await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.Paid, mine: true);

        Assert.Equal(UsdcAmount.Parse("10"), paid.ConfirmedTotal);
        var transfer = Assert.Single(paid.Transfers);
        Assert.Equal(tx, transfer.TxHash);
        Assert.Equal(TransferState.Confirmed, transfer.State);

        // The merchant app (SDK) verified the signatures and parsed typed events, in causal order.
        var webhook = Assert.IsType<InvoiceStatusChangedEvent>(await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.InvoicePaid));
        Assert.Equal(InvoiceStatus.Paid, webhook.Status);
        Assert.Equal(InvoiceStatus.Confirming, webhook.PreviousStatus);
        Assert.Equal("order-s2", webhook.Invoice.ExternalOrderId);
        Assert.Equal(["invoice.confirming", "invoice.paid"], h.Merchant.TypesFor(invoice.Id));
        Assert.Empty(h.Merchant.Rejected);

        var delivered = await h.WaitUntilAllDeliveredAsync(invoice.Id);
        Assert.Equal(["invoice.confirming", "invoice.paid"], delivered.Events.Select(e => e.Type));

        // The payer's page (public status JSON polled by the page) shows the result.
        var token = invoice.PaymentPageUrl.AbsolutePath["/pay/".Length..];
        var status = await h.Http.GetStringAsync(new Uri("/api/public/invoices/" + token, UriKind.Relative));
        Assert.Contains("\"status\":\"paid\"", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3_underpayment_overpayment_and_a_split_payment_end_underpaid_overpaid_and_paid()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var under = await h.CreateInvoiceAsync("10", "order-under");
        var over = await h.CreateInvoiceAsync("10", "order-over");
        var split = await h.CreateInvoiceAsync("10", "order-split");

        await h.PayAsync(under, "4");
        await h.PayAsync(over, "12.5");
        await h.PayAsync(split, "6");
        await h.PayAsync(split, "4");

        var underpaid = await h.WaitForStatusAsync(under.Id, InvoiceStatus.Underpaid, mine: true);
        Assert.Equal(UsdcAmount.Parse("4"), underpaid.ConfirmedTotal);
        Assert.Equal(UsdcAmount.Parse("6"), underpaid.RemainingAmount);

        var overpaid = await h.WaitForStatusAsync(over.Id, InvoiceStatus.Overpaid, mine: true);
        Assert.Equal(UsdcAmount.Parse("2.5"), overpaid.ExcessAmount);

        var paid = await h.WaitForStatusAsync(split.Id, InvoiceStatus.Paid, mine: true);
        Assert.Equal(2, paid.Transfers.Count);
        Assert.Equal(UsdcAmount.Parse("10"), paid.ConfirmedTotal);

        // Each outcome reached the merchant as a verified webhook (FR-11: visible to the merchant).
        await h.WaitForWebhookAsync(under.Id, AcceptCryptoEventTypes.InvoiceUnderpaid);
        await h.WaitForWebhookAsync(over.Id, AcceptCryptoEventTypes.InvoiceOverpaid);
        await h.WaitForWebhookAsync(split.Id, AcceptCryptoEventTypes.InvoicePaid);

        // A top-up completes the underpaid invoice.
        await h.PayAsync(under, "6");
        var completed = await h.WaitForStatusAsync(under.Id, InvoiceStatus.Paid, mine: true);
        Assert.Equal(UsdcAmount.Parse("10"), completed.ConfirmedTotal);
        await h.WaitForWebhookAsync(under.Id, AcceptCryptoEventTypes.InvoicePaid);
    }

    [Fact]
    public async Task S4_unpaid_invoice_expires_and_a_payment_after_the_deadline_ends_paid_late()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var invoice = await h.CreateInvoiceAsync("10", "order-late", expiresIn: TimeSpan.FromSeconds(2));
        var untouched = await h.CreateInvoiceAsync("10", "order-never", expiresIn: TimeSpan.FromSeconds(2));

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        await h.MineAsync(); // a block after the deadline: the scanner has seen the chain past it, expiry may proceed

        await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.Expired);
        var expired = await h.WaitForStatusAsync(untouched.Id, InvoiceStatus.Expired);
        Assert.NotNull(expired.ExpiredAt);
        await h.WaitForWebhookAsync(untouched.Id, AcceptCryptoEventTypes.InvoiceExpired);

        await h.PayAsync(invoice, "10");
        var late = await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.PaidLate, mine: true);
        Assert.False(Assert.Single(late.Transfers).OnTime);

        var webhook = Assert.IsType<InvoiceStatusChangedEvent>(await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.InvoicePaidLate));
        Assert.Equal(InvoiceStatus.PaidLate, webhook.Status);
        Assert.Contains(AcceptCryptoEventTypes.InvoiceExpired, h.Merchant.TypesFor(invoice.Id));
        Assert.DoesNotContain(AcceptCryptoEventTypes.InvoicePaid, h.Merchant.TypesFor(invoice.Id));
        Assert.Equal(InvoiceStatus.Expired, (await h.Client.GetInvoiceAsync(untouched.Id)).Status);
    }

    [Fact]
    public async Task S5_merchant_down_when_the_webhook_is_sent_receives_it_once_it_is_back()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres, merchantUp: false);
        var invoice = await h.CreateInvoiceAsync("10", "order-s5");

        await h.PayAsync(invoice, "10");
        await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.Paid, mine: true);

        // The service keeps trying: failed attempts are recorded, the events stay pending (not lost, not failed).
        // invoice.paid waits behind invoice.confirming (per-invoice order), so the first event carries the retries.
        InvoiceDetails? details = null;
        await Eventually.UntilAsync(async () =>
        {
            details = await h.Client.GetInvoiceAsync(invoice.Id);
            return details.Events.Count == 2 && details.Events[0].Attempts >= 3;
        }, () => "retries of invoice.confirming");
        Assert.All(details!.Events, e => Assert.Equal(EventDeliveryStatus.Pending, e.DeliveryStatus));
        Assert.Contains("Could not connect", details.Events[0].LastError, StringComparison.Ordinal);
        Assert.Empty(h.Merchant.Handled);

        await h.Merchant.StartAsync();

        await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.InvoicePaid);
        var delivered = await h.WaitUntilAllDeliveredAsync(invoice.Id);
        Assert.Equal(["invoice.confirming", "invoice.paid"], h.Merchant.TypesFor(invoice.Id));
        Assert.All(delivered.Events, e => Assert.Equal(200, e.DeliveryAttempts[^1].HttpStatus));
    }

    [Fact]
    public async Task S6_merchant_lists_invoices_by_status_and_sees_the_payment_and_webhook_history()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var from = DateTimeOffset.UtcNow.AddSeconds(-5);
        var first = await h.CreateInvoiceAsync("1", "order-s6-1");
        var second = await h.CreateInvoiceAsync("2", "order-s6-2");
        var third = await h.CreateInvoiceAsync("3", "order-s6-3");

        var tx1 = await h.PayAsync(second, "1.5");
        var tx2 = await h.PayAsync(second, "0.5");
        await h.WaitForStatusAsync(second.Id, InvoiceStatus.Paid, mine: true);
        var details = await h.WaitUntilAllDeliveredAsync(second.Id);

        // List: filter by status, by date, paginate with a cursor.
        var paidPage = await h.Client.ListInvoicesAsync(new ListInvoicesQuery { Status = InvoiceStatus.Paid });
        Assert.Equal(second.Id, Assert.Single(paidPage.Data).Id);
        var pendingPage = await h.Client.ListInvoicesAsync(new ListInvoicesQuery { Status = InvoiceStatus.Pending });
        Assert.Equal([third.Id, first.Id], pendingPage.Data.Select(i => i.Id));
        Assert.Empty((await h.Client.ListInvoicesAsync(new ListInvoicesQuery { CreatedTo = from })).Data);

        var pageOne = await h.Client.ListInvoicesAsync(new ListInvoicesQuery { Limit = 2, CreatedFrom = from });
        Assert.True(pageOne.HasMore);
        var pageTwo = await h.Client.ListInvoicesAsync(new ListInvoicesQuery { Limit = 2, CreatedFrom = from, Cursor = pageOne.NextCursor });
        Assert.False(pageTwo.HasMore);
        Assert.Equal([third.Id, second.Id, first.Id], pageOne.Data.Concat(pageTwo.Data).Select(i => i.Id));

        // History: both transactions, every event with its delivery attempts.
        Assert.Equal([tx1, tx2], details.Transfers.OrderBy(t => t.BlockNumber).Select(t => t.TxHash));
        Assert.All(details.Transfers, t => Assert.Equal(TransferState.Confirmed, t.State));
        // (Whether an intermediate "underpaid" appears depends on when the scanner saw the second transfer.)
        Assert.Equal("invoice.confirming", details.Events[0].Type);
        Assert.Equal("invoice.paid", details.Events[^1].Type);
        Assert.All(details.Events, e =>
        {
            Assert.Equal(EventDeliveryStatus.Delivered, e.DeliveryStatus);
            Assert.NotEmpty(e.DeliveryAttempts);
            Assert.Equal(200, e.DeliveryAttempts[^1].HttpStatus);
        });
    }

    [Fact]
    public async Task S7_transfers_that_match_no_open_invoice_are_listed_as_unmatched()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var paid = await h.CreateInvoiceAsync("5", "order-s7-paid");
        var cancelled = await h.CreateInvoiceAsync("5", "order-s7-cancelled");
        await h.Client.CancelInvoiceAsync(cancelled.Id);

        await h.PayAsync(paid, "5");
        await h.WaitForStatusAsync(paid.Id, InvoiceStatus.Paid, mine: true);

        var extraTx = await h.PayAsync(paid, "1.25");      // paid invoice is closed
        var cancelledTx = await h.PayAsync(cancelled, "5"); // cancelled invoice is closed
        await h.MineAsync(2);

        var unmatched = await Eventually.ValueAsync(async () =>
        {
            var page = await h.Client.ListUnmatchedPaymentsAsync();
            return page.Data.Count >= 2 ? page.Data : null;
        }, () => "two unmatched payments");

        Assert.Equal(2, unmatched.Count);
        var extra = Assert.Single(unmatched, u => u.TxHash == extraTx);
        Assert.Equal(UsdcAmount.Parse("1.25"), extra.Amount);
        Assert.Equal(UnmatchedReason.InvoiceClosed, extra.Reason);
        Assert.Equal(paid.Id, extra.InvoiceId);
        Assert.Equal(paid.Payment.Address, extra.To);
        var toCancelled = Assert.Single(unmatched, u => u.TxHash == cancelledTx);
        Assert.Equal(cancelled.Id, toCancelled.InvoiceId);

        // The invoices themselves are not touched: the paid one is not overpaid, the cancelled one stays cancelled.
        var paidAfter = await h.Client.GetInvoiceAsync(paid.Id);
        Assert.Equal(InvoiceStatus.Paid, paidAfter.Status);
        Assert.Equal(UsdcAmount.Parse("5"), paidAfter.ConfirmedTotal);
        Assert.Equal(InvoiceStatus.Cancelled, (await h.Client.GetInvoiceAsync(cancelled.Id)).Status);
    }

    [Fact]
    public async Task FR8_FR9_service_stopped_during_a_payment_finds_it_after_restart_and_counts_it_once()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var seenBeforeStop = await h.CreateInvoiceAsync("10", "order-restart-1");
        var paidWhileDown = await h.CreateInvoiceAsync("7", "order-restart-2");

        await h.PayAsync(seenBeforeStop, "10");
        await h.WaitForStatusAsync(seenBeforeStop.Id, InvoiceStatus.Confirming);
        await h.WaitForWebhookAsync(seenBeforeStop.Id, AcceptCryptoEventTypes.InvoiceConfirming);

        await h.StopServiceAsync();
        await h.PayAsync(paidWhileDown, "7"); // nobody is watching
        await h.MineAsync(3);                 // both payments get past N while the service is down
        await h.StartServiceAgainAsync();      // resumes from the persisted checkpoint

        var first = await h.WaitForStatusAsync(seenBeforeStop.Id, InvoiceStatus.Paid);
        var second = await h.WaitForStatusAsync(paidWhileDown.Id, InvoiceStatus.Paid);

        // Counted once: one transfer each, totals equal the amounts, one event per transition.
        Assert.Single(first.Transfers);
        Assert.Equal(UsdcAmount.Parse("10"), first.ConfirmedTotal);
        Assert.Single(second.Transfers);
        Assert.Equal(UsdcAmount.Parse("7"), second.ConfirmedTotal);

        // A second restart re-reads nothing as new.
        await h.RestartServiceAsync();
        await h.MineAsync(2);
        var firstDelivered = await h.WaitUntilAllDeliveredAsync(seenBeforeStop.Id);
        var secondDelivered = await h.WaitUntilAllDeliveredAsync(paidWhileDown.Id);
        Assert.Equal(["invoice.confirming", "invoice.paid"], firstDelivered.Events.Select(e => e.Type));
        Assert.Equal(["invoice.confirming", "invoice.paid"], secondDelivered.Events.Select(e => e.Type));
        Assert.Single(firstDelivered.Transfers);
        Assert.Equal(["invoice.confirming", "invoice.paid"], h.Merchant.TypesFor(seenBeforeStop.Id));
        Assert.Empty((await h.Client.ListUnmatchedPaymentsAsync()).Data);
    }

    [Fact]
    public async Task FR10_forced_reorg_rolls_back_a_confirming_payment_and_never_produces_paid()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres, new() { ["Chain:Confirmations"] = "3" });
        var invoice = await h.CreateInvoiceAsync("10", "order-reorg");

        await h.PayAsync(invoice, "10");
        await h.MineAsync(); // 2 of 3 confirmations
        await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.Confirming);
        await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.InvoiceConfirming);

        await chain.Anvil.ReorgAsync(2); // both blocks replaced; the payment is no longer in the canonical chain
        await h.MineAsync(5);            // far beyond N

        var reverted = Assert.IsType<PaymentRevertedEvent>(await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.PaymentReverted));
        Assert.Equal(UsdcAmount.Parse("10"), reverted.Transfer.Amount);
        await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.InvoicePending);

        var after = await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.Pending);
        await h.MineAsync(3);
        await Task.Delay(TimeSpan.FromSeconds(1)); // a few more scanner passes over the new chain
        after = await h.Client.GetInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Pending, after.Status);
        Assert.Equal(UsdcAmount.Zero, after.ConfirmedTotal);
        Assert.Equal(TransferState.Reverted, Assert.Single(after.Transfers).State);
        Assert.Equal(["invoice.confirming", "payment.reverted", "invoice.pending"], after.Events.Select(e => e.Type));
        Assert.DoesNotContain(AcceptCryptoEventTypes.InvoicePaid, h.Merchant.TypesFor(invoice.Id));
    }

    [Fact]
    public async Task FR26_forged_tampered_and_replayed_webhooks_are_rejected_by_the_merchant_app()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var invoice = await h.CreateInvoiceAsync("10", "order-fr26");
        await h.PayAsync(invoice, "10");
        await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.InvoiceConfirming);
        var genuine = h.Merchant.Raw.First(r => r.Body.Length > 0);
        var handledBefore = h.Merchant.Handled.Count(e => e.Invoice.Id == invoice.Id);

        // Forged: a well-formed event signed with a secret the attacker made up.
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var now = nowSeconds.ToString(CultureInfo.InvariantCulture);
        var forgedBody = Encoding.UTF8.GetString(genuine.Body).Replace("invoice.confirming", "invoice.paid", StringComparison.Ordinal);
        var forgedBytes = Encoding.UTF8.GetBytes(forgedBody);
        var forgedSignature = AcceptCryptoWebhookVerifier.ComputeSignatureHeader(["attacker_secret_zzzzzzzzzzzzzzzzzzzzzzzzzz"], nowSeconds, forgedBytes);
        Assert.Equal(HttpStatusCode.BadRequest, await h.Merchant.PostAsync(now, forgedSignature, forgedBytes));

        // Tampered: the genuine signature on an altered body.
        Assert.Equal(HttpStatusCode.BadRequest, await h.Merchant.PostAsync(genuine.Timestamp, genuine.Signature, forgedBytes));

        // Unsigned.
        Assert.Equal(HttpStatusCode.BadRequest, await h.Merchant.PostAsync(now, "", genuine.Body));

        // Replayed as is, right away: detected as a duplicate by event ID (FR-16) — acknowledged, not processed again.
        Assert.Equal(HttpStatusCode.OK, await h.Merchant.PostAsync(genuine.Timestamp, genuine.Signature, genuine.Body));
        Assert.Contains(Guid.Parse(genuine.EventId), h.Merchant.Duplicates);

        // Replayed later (outside the 5-minute tolerance): rejected before anything else.
        h.Merchant.Clock.Offset = TimeSpan.FromMinutes(10);
        Assert.Equal(HttpStatusCode.BadRequest, await h.Merchant.PostAsync(genuine.Timestamp, genuine.Signature, genuine.Body));

        Assert.Equal(
            [WebhookRejectionReason.SignatureMismatch, WebhookRejectionReason.SignatureMismatch, WebhookRejectionReason.MissingHeaders, WebhookRejectionReason.TimestampOutsideTolerance],
            h.Merchant.Rejected);
        Assert.Equal(handledBefore, h.Merchant.Handled.Count(e => e.Invoice.Id == invoice.Id));
        Assert.DoesNotContain(AcceptCryptoEventTypes.InvoicePaid, h.Merchant.TypesFor(invoice.Id));
    }
}
