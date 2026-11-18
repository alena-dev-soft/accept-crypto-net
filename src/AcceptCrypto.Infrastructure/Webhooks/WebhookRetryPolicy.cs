using AcceptCrypto.Infrastructure.Chain.Scanning;

namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>
/// Retry schedule of webhook deliveries (FR-14): exponential backoff with ±20 % jitter
/// (<see cref="WebhookOptions.InitialRetryDelay"/>·2^(n-1), capped at <see cref="WebhookOptions.MaxRetryDelay"/>),
/// limited by <see cref="WebhookOptions.MaxAttempts"/> and by <see cref="WebhookOptions.RetryWindow"/> counted from
/// the start of the retry cycle (creation or last manual resend).
/// </summary>
public static class WebhookRetryPolicy
{
    /// <summary>
    /// When to try again after failed attempt number <paramref name="failedAttempts"/> (1-based, in this cycle);
    /// <c>null</c> = give up, the event is marked failed.
    /// </summary>
    public static DateTimeOffset? NextAttemptAt(int failedAttempts, DateTimeOffset cycleStartedAt, DateTimeOffset now, WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (failedAttempts >= options.MaxAttempts)
        {
            return null;
        }

        var next = now + Backoff.Delay(failedAttempts, options.InitialRetryDelay, options.MaxRetryDelay);
        return next > cycleStartedAt + options.RetryWindow ? null : next;
    }

    /// <summary>
    /// The nominal schedule without jitter: delays before attempts 2..N that fit into the window. For docs and tests.
    /// </summary>
    public static IReadOnlyList<TimeSpan> NominalDelays(WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var delays = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;
        for (var failed = 1; failed < options.MaxAttempts; failed++)
        {
            var ms = Math.Min(options.MaxRetryDelay.TotalMilliseconds, options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failed - 1, 30)));
            var delay = TimeSpan.FromMilliseconds(ms);
            if (elapsed + delay > options.RetryWindow)
            {
                break;
            }

            elapsed += delay;
            delays.Add(delay);
        }

        return delays;
    }
}
