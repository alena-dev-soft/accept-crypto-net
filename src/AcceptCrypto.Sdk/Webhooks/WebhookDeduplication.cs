using System.Collections.Concurrent;

namespace AcceptCrypto.Sdk.Webhooks;

/// <summary>Outcome of <see cref="IWebhookEventDeduplicator.TryBeginAsync"/>.</summary>
public enum WebhookEventClaim
{
    /// <summary>First time: process the event.</summary>
    New = 1,

    /// <summary>Already processed successfully: acknowledge without processing again.</summary>
    AlreadyProcessed,

    /// <summary>Another request is processing it right now: answer 409 so the service retries later.</summary>
    InProgress,
}

/// <summary>
/// Detects duplicate deliveries by event ID (FR-16): the service delivers at least once, so the same event can arrive
/// again. The default implementation is in memory (one process); for several instances or across restarts, implement
/// this over your database (e.g. a unique index on the event ID) and register it in DI.
/// </summary>
public interface IWebhookEventDeduplicator
{
    /// <summary>Claims the event for processing.</summary>
    ValueTask<WebhookEventClaim> TryBeginAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken);

    /// <summary>The handler succeeded: later deliveries of this event are duplicates.</summary>
    ValueTask CompleteAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken);

    /// <summary>The handler failed: release the claim so a retry processes the event again.</summary>
    ValueTask AbandonAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken);
}

/// <summary>
/// In-memory <see cref="IWebhookEventDeduplicator"/>: remembers processed event IDs for <see cref="Retention"/>
/// (default 7 days, at most <see cref="Capacity"/> IDs). Good for a single instance; lost on restart.
/// </summary>
public sealed class InMemoryWebhookEventDeduplicator : IWebhookEventDeduplicator
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly TimeProvider _time;
    private long _lastPruneTicks;

    /// <summary>Creates the deduplicator.</summary>
    public InMemoryWebhookEventDeduplicator(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>How long a processed ID is remembered.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Maximum remembered IDs; the oldest are dropped first.</summary>
    public int Capacity { get; init; } = 100_000;

    /// <summary>A claim older than this is considered abandoned (the handler crashed without releasing it).</summary>
    public TimeSpan InProgressTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public ValueTask<WebhookEventClaim> TryBeginAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);
        var now = _time.GetUtcNow();
        Prune(now);
        while (true)
        {
            if (_entries.TryAdd(webhookEvent.Id, new Entry(false, now)))
            {
                return ValueTask.FromResult(WebhookEventClaim.New);
            }

            if (!_entries.TryGetValue(webhookEvent.Id, out var existing))
            {
                continue;
            }

            if (existing.Completed)
            {
                return ValueTask.FromResult(WebhookEventClaim.AlreadyProcessed);
            }

            if (now - existing.At < InProgressTimeout)
            {
                return ValueTask.FromResult(WebhookEventClaim.InProgress);
            }

            if (_entries.TryUpdate(webhookEvent.Id, new Entry(false, now), existing))
            {
                return ValueTask.FromResult(WebhookEventClaim.New);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);
        _entries[webhookEvent.Id] = new Entry(true, _time.GetUtcNow());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AbandonAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookEvent);
        if (_entries.TryGetValue(webhookEvent.Id, out var entry) && !entry.Completed)
        {
            _entries.TryRemove(new KeyValuePair<Guid, Entry>(webhookEvent.Id, entry));
        }

        return ValueTask.CompletedTask;
    }

    private void Prune(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last < TimeSpan.TicksPerMinute && _entries.Count <= Capacity)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last)
        {
            return; // Another request prunes right now.
        }

        foreach (var (id, entry) in _entries)
        {
            if (entry.Completed && now - entry.At > Retention)
            {
                _entries.TryRemove(new KeyValuePair<Guid, Entry>(id, entry));
            }
        }

        var excess = _entries.Count - Capacity;
        if (excess > 0)
        {
            foreach (var (id, entry) in _entries.Where(e => e.Value.Completed).OrderBy(e => e.Value.At).Take(excess).ToList())
            {
                _entries.TryRemove(new KeyValuePair<Guid, Entry>(id, entry));
            }
        }
    }

    private sealed record Entry(bool Completed, DateTimeOffset At);
}
