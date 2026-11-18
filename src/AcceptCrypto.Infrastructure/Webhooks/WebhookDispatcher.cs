using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using AcceptCrypto.Infrastructure.Merchant;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>
/// Delivers webhook events from the transactional outbox (FR-12–FR-16). One pass claims due events
/// (<c>FOR UPDATE SKIP LOCKED</c> + lease, safe with several service instances), POSTs each signed body to the
/// merchant's webhook URL and records the attempt: 2xx = delivered, anything else (status, timeout, connection
/// error, SSRF refusal) = retry with backoff or, past the limit, failed. A merchant outage only schedules retries;
/// it never throws out of the pass (NFR-4).
/// </summary>
public sealed partial class WebhookDispatcher(
    IOutboxRepository outbox,
    IHttpClientFactory httpClientFactory,
    WebhookUrlGuard urlGuard,
    IOptionsMonitor<MerchantOptions> merchantOptions,
    IOptionsMonitor<WebhookOptions> webhookOptions,
    WebhookMetrics metrics,
    TimeProvider clock,
    ILogger<WebhookDispatcher> logger)
{
    /// <summary>Name of the <see cref="HttpClient"/> (primary handler = the SSRF-guarded handler).</summary>
    public const string HttpClientName = "AcceptCrypto.Webhooks";

    /// <summary>User-Agent of webhook requests.</summary>
    public const string UserAgent = "AcceptCrypto.NET-Webhooks/1.0";

    private const int MaxErrorLength = 2048;
    private const int MaxRecordRetries = 3;
    private static readonly TimeSpan StatsRefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>Runs one pass; returns the number of events claimed (a full batch means: run again right away).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var options = webhookOptions.CurrentValue;
        var now = clock.GetUtcNow();
        var claimed = await outbox.ClaimDueAsync(now, now + options.ClaimLease, options.BatchSize, cancellationToken).ConfigureAwait(false);
        if (claimed.Count > 0)
        {
            await Parallel.ForEachAsync(
                claimed,
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelDeliveries, CancellationToken = cancellationToken },
                async (outboxEvent, ct) => await DeliverAsync(outboxEvent, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        if (claimed.Count > 0 || metrics.StatsRefreshedAt is not { } refreshed || clock.GetUtcNow() - refreshed >= StatsRefreshInterval)
        {
            metrics.Update(await outbox.GetQueueStatsAsync(clock.GetUtcNow(), cancellationToken).ConfigureAwait(false));
        }

        return claimed.Count;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any send failure is a failed attempt, recorded and retried (NFR-4).")]
    private async Task DeliverAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        var options = webhookOptions.CurrentValue;
        var merchant = merchantOptions.CurrentValue;
        var started = clock.GetTimestamp();
        var attemptedAt = clock.GetUtcNow();
        int? httpStatus = null;
        string? error = null;

        try
        {
            var url = merchant.WebhookUrl ?? throw new InvalidOperationException("Merchant:WebhookUrl is not configured.");
            var check = await urlGuard.CheckAsync(url, cancellationToken).ConfigureAwait(false);
            if (!check.Allowed)
            {
                error = "Webhook URL refused: " + check.Reason;
            }
            else
            {
                using var request = CreateRequest(url, outboxEvent, merchant, attemptedAt);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.RequestTimeout);
                var client = httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                httpStatus = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    error = ("HTTP " + httpStatus.Value.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase).TrimEnd();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: nothing is recorded; the claim lease runs out and the event is delivered later.
            throw;
        }
        catch (OperationCanceledException)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Timed out after {options.RequestTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }

        var duration = clock.GetElapsedTime(started);
        var finishedAt = clock.GetUtcNow();
        if (error is not null && error.Length > MaxErrorLength)
        {
            error = error[..MaxErrorLength];
        }

        var updated = await RecordAsync(outboxEvent.EventId, httpStatus, error, attemptedAt, finishedAt, duration, options, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return;
        }

        if (error is null)
        {
            metrics.RecordAttempt("delivered", duration);
            LogDelivered(logger, updated.EventId, updated.Type, httpStatus ?? 0, updated.Attempts);
        }
        else if (updated.Status == OutboxEventStatus.Failed)
        {
            metrics.RecordAttempt("failed", duration);
            LogFailed(logger, updated.EventId, updated.Type, updated.Attempts, error);
        }
        else
        {
            metrics.RecordAttempt("retry", duration);
            LogRetry(logger, updated.EventId, updated.Type, updated.Attempts, error, updated.NextAttemptAt);
        }
    }

    private static HttpRequestMessage CreateRequest(Uri url, OutboxEvent outboxEvent, MerchantOptions merchant, DateTimeOffset now)
    {
        // The timestamp is the time of this attempt: retries are re-signed, so the receiver's tolerance window
        // (SDK default 5 min) applies to the delivery, not to the event's age.
        var timestamp = now.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(outboxEvent.Payload)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventIdHeader, outboxEvent.EventId.ToString("D"));
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventTypeHeader, outboxEvent.Type);
        request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            WebhookSignature.SignatureHeader,
            WebhookSignature.CreateHeader(merchant.WebhookSecrets, timestamp, outboxEvent.Payload));
        return request;
    }

    private async Task<OutboxEvent?> RecordAsync(
        Guid eventId,
        int? httpStatus,
        string? error,
        DateTimeOffset attemptedAt,
        DateTimeOffset finishedAt,
        TimeSpan duration,
        WebhookOptions options,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await outbox.UpdateAsync(
                    eventId,
                    e =>
                    {
                        if (error is null)
                        {
                            e.RecordSuccess(attemptedAt, httpStatus ?? 0, duration);
                        }
                        else
                        {
                            var next = WebhookRetryPolicy.NextAttemptAt(e.Attempts + 1, e.RetryCycleStartedAt, finishedAt, options);
                            e.RecordFailure(attemptedAt, httpStatus, error, duration, next);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRecordRetries)
            {
                // A manual resend touched the row meanwhile: reload and record again.
            }
        }
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 3; e = e.InnerException)
        {
            if (e is WebhookUrlBlockedException)
            {
                return "Webhook URL refused: " + e.Message;
            }

            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message))
            {
                parts.Add(e.Message);
            }
        }

        return ex.GetType().Name + ": " + string.Join(" → ", parts);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {EventId} ({EventType}) delivered: HTTP {HttpStatus}, attempt {Attempt}")]
    private static partial void LogDelivered(ILogger logger, Guid eventId, string eventType, int httpStatus, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {EventId} ({EventType}) attempt {Attempt} failed: {Error}; next attempt at {NextAttemptAt:o}")]
    private static partial void LogRetry(ILogger logger, Guid eventId, string eventType, int attempt, string error, DateTimeOffset nextAttemptAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook {EventId} ({EventType}) marked failed after {Attempt} attempts: {Error}; it can be resent via the API")]
    private static partial void LogFailed(ILogger logger, Guid eventId, string eventType, int attempt, string error);
}
