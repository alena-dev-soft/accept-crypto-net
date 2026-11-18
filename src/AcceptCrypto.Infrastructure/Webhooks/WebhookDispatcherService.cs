using System.Diagnostics.CodeAnalysis;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>
/// Runs <see cref="WebhookDispatcher"/> passes forever: right away again while passes claim events, otherwise every
/// <see cref="WebhookOptions.PollInterval"/>. A database outage is logged and retried with backoff (capped at one
/// minute); it never stops the host and loses nothing — events stay in the outbox (NFR-4).
/// </summary>
public sealed partial class WebhookDispatcherService(
    WebhookDispatcher dispatcher,
    IOptionsMonitor<WebhookOptions> options,
    TimeProvider clock,
    ILogger<WebhookDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan MaxErrorDelay = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The worker must survive any failure (NFR-4); it is logged and retried.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.CurrentValue.MaxAttempts, options.CurrentValue.RetryWindow);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var claimed = await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                failures = 0;
                if (claimed > 0)
                {
                    // More may be due right away (a full batch, or the next event of an invoice just delivered).
                    continue;
                }

                delay = options.CurrentValue.PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                delay = Backoff.Delay(failures, options.CurrentValue.DispatcherErrorDelay, MaxErrorDelay);
                LogPassFailed(logger, ex, failures, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook dispatcher started (max {MaxAttempts} attempts within {RetryWindow})")]
    private static partial void LogStarted(ILogger logger, int maxAttempts, TimeSpan retryWindow);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook dispatcher pass failed ({Failures} in a row); retrying in {DelaySeconds:0.0} s")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, int failures, double delaySeconds);
}
