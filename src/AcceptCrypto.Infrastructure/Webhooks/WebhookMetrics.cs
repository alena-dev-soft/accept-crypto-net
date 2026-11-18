using System.Diagnostics.Metrics;
using AcceptCrypto.Infrastructure.Persistence;

namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>
/// Webhook metrics (NFR-7), meter <see cref="MeterName"/>: queue length (pending / due / failed), age of the oldest
/// pending event, delivery attempts by outcome and their duration. Queue numbers are refreshed by the dispatcher.
/// </summary>
public sealed class WebhookMetrics
{
    /// <summary>Name of the <see cref="Meter"/>.</summary>
    public const string MeterName = "AcceptCrypto.Webhooks";

    private readonly TimeProvider _clock;
    private readonly Counter<long> _attempts;
    private readonly Histogram<double> _duration;
    private OutboxQueueStats? _stats;

    /// <summary>Creates the instruments.</summary>
    public WebhookMetrics(IMeterFactory meterFactory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _clock = clock;
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("acceptcrypto.webhooks.queue_length", () => Stats?.Pending ?? 0, "{event}", "Webhook events waiting for delivery (due or scheduled for a retry).");
        meter.CreateObservableGauge("acceptcrypto.webhooks.due", () => Stats?.Due ?? 0, "{event}", "Pending webhook events whose next attempt is due.");
        meter.CreateObservableGauge("acceptcrypto.webhooks.failed", () => Stats?.Failed ?? 0, "{event}", "Webhook events that exhausted their retries.");
        meter.CreateObservableGauge("acceptcrypto.webhooks.oldest_pending_age", () => OldestPendingAge.TotalSeconds, "s", "Age of the oldest pending webhook event.");
        _attempts = meter.CreateCounter<long>("acceptcrypto.webhooks.attempts", "{attempt}", "Webhook delivery attempts by outcome.");
        _duration = meter.CreateHistogram<double>("acceptcrypto.webhooks.attempt_duration", "s", "Duration of webhook delivery attempts.");
    }

    /// <summary>Latest queue statistics (null until the dispatcher's first pass).</summary>
    public OutboxQueueStats? Stats => Volatile.Read(ref _stats);

    /// <summary>When <see cref="Stats"/> were last refreshed.</summary>
    public DateTimeOffset? StatsRefreshedAt { get; private set; }

    private TimeSpan OldestPendingAge =>
        Stats?.OldestPendingCreatedAt is { } oldest ? _clock.GetUtcNow() - oldest : TimeSpan.Zero;

    /// <summary>Stores fresh queue statistics.</summary>
    public void Update(OutboxQueueStats stats)
    {
        Volatile.Write(ref _stats, stats);
        StatsRefreshedAt = _clock.GetUtcNow();
    }

    /// <summary>Counts one attempt; <paramref name="outcome"/> is <c>delivered</c>, <c>retry</c> or <c>failed</c>.</summary>
    public void RecordAttempt(string outcome, TimeSpan duration)
    {
        _attempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _duration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
    }
}
