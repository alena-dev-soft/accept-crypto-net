using System.Net;
using System.Text.Json;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.Infrastructure.Webhooks;
using AcceptCrypto.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AcceptCrypto.IntegrationTests.Webhooks;

/// <summary>
/// The outbox dispatcher against a real PostgreSQL and a real HTTP receiver: signature (FR-13), unique event ID
/// (FR-16), retries with backoff while the merchant is down and delivery after it is back (S5, FR-14), failure after
/// the limit, manual resend under the same event ID (FR-15), parallel dispatchers (NFR-3), metrics (NFR-7).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class WebhookDeliveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Delivers_the_stored_payload_signed_with_timestamp_and_event_id_headers()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        await using var harness = new DispatcherHarness(store, receiver.Url);
        var evt = await harness.CreateEventAsync();

        Assert.Equal(1, await harness.RunOnceAsync());

        var received = Assert.Single(receiver.Received);
        Assert.Equal(evt.Payload, received.Body);
        Assert.Equal(evt.EventId.ToString(), received.EventId);
        Assert.Equal("invoice.confirming", received.EventType);
        Assert.Equal("application/json; charset=utf-8", received.ContentType);
        Assert.StartsWith("AcceptCrypto.NET-Webhooks/", received.UserAgent, StringComparison.Ordinal);
        var timestamp = long.Parse(received.Timestamp, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(timestamp, store.Now.ToUnixTimeSeconds() - 2, store.Now.ToUnixTimeSeconds());
        Assert.Equal("v1=" + WebhookSignature.Compute(DispatcherHarness.Secret, timestamp, received.Body), received.Signature);

        var stored = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(OutboxEventStatus.Delivered, stored.Status);
        Assert.NotNull(stored.DeliveredAt);
        var attempt = Assert.Single(stored.DeliveryAttempts);
        Assert.Equal(200, attempt.HttpStatus);
        Assert.Null(attempt.Error);

        // Delivered events are not sent again.
        Assert.Equal(0, await harness.RunOnceAsync());
        Assert.Single(receiver.Received);
    }

    [Fact]
    public async Task During_secret_rotation_the_service_signs_with_the_new_and_the_previous_secret()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?>
        {
            ["Merchant:PreviousWebhookSecret"] = DispatcherHarness.PreviousSecret,
        });
        await harness.CreateEventAsync();

        await harness.RunOnceAsync();

        var received = Assert.Single(receiver.Received);
        var timestamp = long.Parse(received.Timestamp, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            $"v1={WebhookSignature.Compute(DispatcherHarness.Secret, timestamp, received.Body)},v1={WebhookSignature.Compute(DispatcherHarness.PreviousSecret, timestamp, received.Body)}",
            received.Signature);
    }

    [Fact]
    public async Task S5_merchant_down_is_retried_with_backoff_and_delivered_once_it_is_back()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = WebhookReceiver.Create(); // not listening: connection refused
        await using var harness = new DispatcherHarness(store, receiver.Url);
        var evt = await harness.CreateEventAsync();

        await harness.RunOnceAsync();
        var afterFirst = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(OutboxEventStatus.Pending, afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempts);
        Assert.InRange(afterFirst.NextAttemptAt - store.Now, TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(36)); // 30 s ± 20 %
        var failed = Assert.Single(afterFirst.DeliveryAttempts);
        Assert.Null(failed.HttpStatus);
        Assert.False(string.IsNullOrEmpty(failed.Error));

        // Not due yet: nothing is sent.
        Assert.Equal(0, await harness.RunOnceAsync());

        // Second attempt, still down: the delay doubles.
        store.Clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(1, await harness.RunOnceAsync());
        var afterSecond = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(2, afterSecond.Attempts);
        Assert.InRange(afterSecond.NextAttemptAt - store.Now, TimeSpan.FromSeconds(47), TimeSpan.FromSeconds(73)); // 60 s ± 20 %

        // The merchant comes back; the next due attempt delivers.
        await receiver.StartAsync();
        store.Clock.Advance(TimeSpan.FromSeconds(80));
        Assert.Equal(1, await harness.RunOnceAsync());

        var delivered = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(OutboxEventStatus.Delivered, delivered.Status);
        Assert.Equal([null, null, 200], delivered.DeliveryAttempts.Select(a => a.HttpStatus));
        var received = Assert.Single(receiver.Received);
        Assert.Equal(evt.EventId.ToString(), received.EventId);
        Assert.Equal(evt.Payload, received.Body);
    }

    [Fact]
    public async Task Non_2xx_answers_are_retried_and_the_event_is_failed_after_the_limit_then_a_resend_keeps_the_event_id()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        receiver.Status = HttpStatusCode.InternalServerError;
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?> { ["Webhooks:MaxAttempts"] = "3" });
        var evt = await harness.CreateEventAsync();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, await harness.RunOnceAsync());
            store.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        var failed = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(OutboxEventStatus.Failed, failed.Status);
        Assert.Equal(3, failed.Attempts);
        Assert.Equal("HTTP 500 Internal Server Error", failed.LastError);
        Assert.All(failed.DeliveryAttempts, a => Assert.Equal(500, a.HttpStatus));
        Assert.Equal(0, await harness.RunOnceAsync()); // failed events are not picked up again
        Assert.Equal(3, receiver.Received.Count);

        // FR-15: manual resend (what POST /v1/events/{id}/resend does) — same ID, same bytes, fresh retry budget.
        receiver.Status = HttpStatusCode.NoContent;
        await store.Outbox.UpdateAsync(evt.EventId, e => e.Resend(store.Now));
        Assert.Equal(1, await harness.RunOnceAsync());

        var resent = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(OutboxEventStatus.Delivered, resent.Status);
        Assert.Equal([500, 500, 500, 204], resent.DeliveryAttempts.Select(a => a.HttpStatus));
        Assert.Equal(4, receiver.Received.Count);
        Assert.Single(receiver.Received.Select(r => r.EventId).Distinct());
        Assert.Single(receiver.Received.Select(r => r.Body).Distinct());
        Assert.Equal(evt.EventId.ToString(), receiver.Received.Last().EventId);
    }

    [Fact]
    public async Task The_event_is_failed_when_the_retry_window_is_over_even_below_the_attempt_limit()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = WebhookReceiver.Create();
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?>
        {
            ["Webhooks:MaxAttempts"] = "100",
            ["Webhooks:RetryWindow"] = "00:05:00",
        });
        var evt = await harness.CreateEventAsync();

        var passes = 0;
        OutboxEvent current;
        do
        {
            await harness.RunOnceAsync();
            store.Clock.Advance(TimeSpan.FromMinutes(10));
            current = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        }
        while (current.Status == OutboxEventStatus.Pending && ++passes < 20);

        Assert.Equal(OutboxEventStatus.Failed, current.Status);
        Assert.InRange(current.Attempts, 1, 4); // 30 s, 60 s, 120 s... until 5 min would be exceeded
    }

    [Fact]
    public async Task A_slow_merchant_times_out_and_is_retried()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        receiver.Delay = TimeSpan.FromSeconds(5);
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?> { ["Webhooks:RequestTimeout"] = "00:00:01" });
        var evt = await harness.CreateEventAsync();

        await harness.RunOnceAsync();

        var stored = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(OutboxEventStatus.Pending, stored.Status);
        Assert.Equal("Timed out after 1 s", stored.LastError);
    }

    [Fact]
    public async Task A_webhook_url_on_the_internal_network_is_refused_without_a_request()
    {
        await using var store = await postgres.CreateStoreAsync();
        // A public-looking https host whose DNS answer points into the private network (e.g. after rebinding).
        var resolver = new FixedResolver(IPAddress.Parse("10.0.0.5"));
        await using var harness = new DispatcherHarness(store, new Uri("https://merchant.example/webhooks"), allowlist: false, resolver: resolver);
        var evt = await harness.CreateEventAsync();

        await harness.RunOnceAsync();

        var stored = await harness.ReloadAsync(evt.EventId, evt.InvoiceId);
        Assert.Equal(OutboxEventStatus.Pending, stored.Status);
        Assert.StartsWith("Webhook URL refused", stored.LastError, StringComparison.Ordinal);
        Assert.Null(Assert.Single(stored.DeliveryAttempts).HttpStatus);
    }

    private sealed class FixedResolver(IPAddress address) : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(new[] { address });
    }

    [Fact]
    public async Task Parallel_dispatchers_deliver_every_event_exactly_once()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        receiver.Delay = TimeSpan.FromMilliseconds(20);
        var overrides = new Dictionary<string, string?> { ["Webhooks:BatchSize"] = "3" };
        var harnesses = Enumerable.Range(0, 4).Select(_ => new DispatcherHarness(store, receiver.Url, overrides)).ToList();
        var events = new List<OutboxEvent>();
        for (var i = 0; i < 24; i++)
        {
            events.Add(await harnesses[0].CreateEventAsync());
        }

        // Four dispatchers (as four service instances would) drain the queue concurrently.
        await Task.WhenAll(harnesses.Select(async h =>
        {
            while (await h.RunOnceAsync() > 0)
            {
            }
        }));

        Assert.Equal(24, receiver.Received.Count);
        Assert.Equal(events.Select(e => e.EventId.ToString()).Order(), receiver.Received.Select(r => r.EventId).Order());
        Assert.Equal(24, await store.ScalarAsync("SELECT count(*) FROM outbox_events WHERE status = 'Delivered'"));
        Assert.Equal(24, await store.ScalarAsync("SELECT count(*) FROM webhook_delivery_attempts"));
        foreach (var h in harnesses)
        {
            await h.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_claimed_event_is_hidden_from_other_dispatchers_until_its_lease_runs_out()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        await using var harness = new DispatcherHarness(store, receiver.Url);
        var evt = await harness.CreateEventAsync();

        // A dispatcher claims it and dies before recording anything.
        var claimed = await store.Outbox.ClaimDueAsync(store.Now, store.Now.AddMinutes(1), 10);
        Assert.Equal(evt.EventId, Assert.Single(claimed).EventId);
        Assert.Empty(await store.Outbox.ClaimDueAsync(store.Now, store.Now.AddMinutes(1), 10));
        Assert.Equal(0, await harness.RunOnceAsync());

        // After the lease the event is delivered (at-least-once; the receiver dedupes by event ID).
        store.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await harness.RunOnceAsync());
        Assert.Equal(evt.EventId.ToString(), Assert.Single(receiver.Received).EventId);
    }

    [Fact]
    public async Task Events_of_one_invoice_are_delivered_in_causal_order_and_a_retrying_event_holds_back_the_next()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        receiver.Status = HttpStatusCode.ServiceUnavailable;
        await using var harness = new DispatcherHarness(store, receiver.Url);
        var invoice = await store.CreateInvoiceAsync("10");
        var transfer = store.Transfer(invoice.DepositAddress, "10");
        await store.RecordAsync(invoice.Id, transfer);
        await store.ConfirmAsync(invoice.Id, transfer.Id);

        // Only the head of the invoice's queue is claimed; while it is retried, invoice.paid waits behind it.
        Assert.Equal(1, await harness.RunOnceAsync());
        store.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await harness.RunOnceAsync());

        receiver.Status = HttpStatusCode.OK;
        store.Clock.Advance(TimeSpan.FromMinutes(1));
        while (await harness.RunOnceAsync() > 0)
        {
        }

        Assert.Equal(["invoice.confirming", "invoice.confirming", "invoice.paid"], receiver.Received.Select(r => r.EventType));
        using var paid = JsonDocument.Parse(receiver.Received.Last().Body);
        Assert.Equal("paid", paid.RootElement.GetProperty("data").GetProperty("invoice").GetProperty("status").GetString());
        Assert.Equal("confirming", paid.RootElement.GetProperty("data").GetProperty("previous_status").GetString());
    }

    [Fact]
    public async Task A_failed_event_does_not_block_later_events_of_the_invoice()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = await WebhookReceiver.StartNewAsync();
        receiver.Status = HttpStatusCode.BadRequest;
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?> { ["Webhooks:MaxAttempts"] = "1" });
        var invoice = await store.CreateInvoiceAsync("10");
        var transfer = store.Transfer(invoice.DepositAddress, "10");
        await store.RecordAsync(invoice.Id, transfer);
        await harness.RunOnceAsync(); // invoice.confirming fails for good

        receiver.Status = HttpStatusCode.OK;
        await store.ConfirmAsync(invoice.Id, transfer.Id);
        Assert.Equal(1, await harness.RunOnceAsync());
        Assert.Equal("invoice.paid", receiver.Received.Last().EventType);
    }

    [Fact]
    public async Task Queue_metrics_report_pending_due_and_failed_events()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = WebhookReceiver.Create();
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?> { ["Webhooks:MaxAttempts"] = "1" });
        await harness.CreateEventAsync();
        await harness.RunOnceAsync(); // fails at once (limit 1)
        await harness.CreateEventAsync();
        await harness.CreateEventAsync();

        var stats = await store.Outbox.GetQueueStatsAsync(store.Now);
        Assert.Equal(new OutboxQueueStats(2, 2, 1, stats.OldestPendingCreatedAt), stats);
        Assert.NotNull(stats.OldestPendingCreatedAt);

        await harness.RunOnceAsync();
        Assert.Equal(3, harness.Metrics.Stats!.Failed);
        Assert.Equal(0, harness.Metrics.Stats.Pending);
    }

    [Fact]
    public async Task The_hosted_dispatcher_keeps_running_while_the_merchant_is_down_and_delivers_when_it_is_back()
    {
        await using var store = await postgres.CreateStoreAsync();
        await using var receiver = WebhookReceiver.Create();
        await using var harness = new DispatcherHarness(store, receiver.Url, new Dictionary<string, string?>
        {
            ["Webhooks:PollInterval"] = "00:00:00.050",
            ["Webhooks:InitialRetryDelay"] = "00:00:00.100",
            ["Webhooks:MaxRetryDelay"] = "00:00:00.200",
            ["Webhooks:MaxAttempts"] = "1000",
        });
        var evt = await harness.CreateEventAsync();
        var service = ActivatorUtilities.CreateInstance<WebhookDispatcherService>(harness.Services);

        // The fake clock auto-advances 1 ms per read; advance it in the background so retries come due.
        using var cts = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                store.Clock.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(20, CancellationToken.None);
            }
        });

        await ((IHostedService)service).StartAsync(CancellationToken.None);
        await WaitUntilAsync(async () => (await harness.ReloadAsync(evt.EventId, evt.InvoiceId)).Attempts >= 2);
        await receiver.StartAsync();
        await WaitUntilAsync(async () => (await harness.ReloadAsync(evt.EventId, evt.InvoiceId)).Status == OutboxEventStatus.Delivered);
        await ((IHostedService)service).StopAsync(CancellationToken.None);
        await cts.CancelAsync();
        await ticker;
        service.Dispose();

        Assert.Equal(evt.EventId.ToString(), Assert.Single(receiver.Received).EventId);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached within 30 s.");
            await Task.Delay(50);
        }
    }
}
