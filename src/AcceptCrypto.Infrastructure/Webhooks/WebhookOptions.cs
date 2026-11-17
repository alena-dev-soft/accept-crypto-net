namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>
/// Webhook delivery settings (section <c>Webhooks</c>): retry schedule (FR-14) and the SSRF allowlist (NFR-6).
/// </summary>
public sealed class WebhookOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Host names (or IP literals) that may resolve to private / loopback addresses and may use plain http, e.g. the
    /// demo shop on the Docker network (<c>demo-shop</c>). Exact, case-insensitive match. Empty by default.
    /// </summary>
    public IList<string> AllowedHosts { get; } = [];

    /// <summary>Delay before the first retry; doubles with every failed attempt (exponential backoff).</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of a single retry delay.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum delivery attempts (including the first) before the event is marked failed.</summary>
    public int MaxAttempts { get; set; } = 30;

    /// <summary>Total retry window counted from the event's creation (default ~24 h); after it the event is failed.</summary>
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>HTTP timeout of one delivery attempt.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>True if <paramref name="host"/> is on <see cref="AllowedHosts"/>.</summary>
    public bool IsAllowlisted(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var normalized = host.Trim('[', ']').TrimEnd('.');
        return AllowedHosts.Any(h => string.Equals(h.Trim('[', ']').TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Checks the retry schedule; returns false with a message (no values from secrets) when invalid.</summary>
    public bool IsValid(out string? error)
    {
        error = null;
        if (InitialRetryDelay <= TimeSpan.Zero || MaxRetryDelay < InitialRetryDelay)
        {
            error = "Webhooks: require 0 < InitialRetryDelay <= MaxRetryDelay.";
        }
        else if (MaxAttempts < 1)
        {
            error = "Webhooks: MaxAttempts must be at least 1.";
        }
        else if (RetryWindow <= TimeSpan.Zero)
        {
            error = "Webhooks: RetryWindow must be positive.";
        }
        else if (RequestTimeout <= TimeSpan.Zero)
        {
            error = "Webhooks: RequestTimeout must be positive.";
        }
        else if (AllowedHosts.Any(string.IsNullOrWhiteSpace))
        {
            error = "Webhooks: AllowedHosts must not contain empty entries.";
        }

        return error is null;
    }
}
