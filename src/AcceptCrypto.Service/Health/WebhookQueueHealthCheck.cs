using AcceptCrypto.Infrastructure.Webhooks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.Health;

/// <summary>
/// Webhook queue state (NFR-7) in <c>/health</c>: Degraded when events exhausted their retries (failed, waiting for a
/// manual resend) or the oldest pending event is older than <see cref="StaleAfter"/> (merchant down for long).
/// Never Unhealthy: an unavailable merchant must not get the service restarted (NFR-4).
/// </summary>
internal sealed class WebhookQueueHealthCheck(WebhookMetrics metrics, IOptions<WebhookOptions> options, TimeProvider clock) : IHealthCheck
{
    /// <summary>Age of the oldest pending event from which the check reports Degraded.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.DispatcherEnabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Webhook dispatcher disabled."));
        }

        if (metrics.Stats is not { } stats)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Webhook dispatcher starting."));
        }

        var oldestAge = stats.OldestPendingCreatedAt is { } oldest ? clock.GetUtcNow() - oldest : TimeSpan.Zero;
        var data = new Dictionary<string, object>
        {
            ["pending"] = stats.Pending,
            ["due"] = stats.Due,
            ["failed"] = stats.Failed,
            ["oldest_pending_age_seconds"] = (long)oldestAge.TotalSeconds,
        };
        HealthCheckResult result;
        if (stats.Failed > 0)
        {
            result = HealthCheckResult.Degraded("Some webhook events failed after all retries; resend them via the API.", data: data);
        }
        else if (oldestAge > StaleAfter)
        {
            result = HealthCheckResult.Degraded("Webhook events have been pending for a long time (merchant unreachable?).", data: data);
        }
        else
        {
            result = HealthCheckResult.Healthy("Webhook queue draining.", data);
        }

        return Task.FromResult(result);
    }
}
