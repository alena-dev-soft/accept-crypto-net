using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Chain.Scanning;

/// <summary>
/// Runs <see cref="ChainScanner"/> passes forever: every <see cref="ChainOptions.PollInterval"/> when healthy,
/// with exponential backoff (+ jitter) after failures. An RPC or database outage is logged and retried; it never
/// stops the host and loses nothing, because the next pass resumes from the persisted checkpoint (NFR-4, FR-9).
/// </summary>
public sealed partial class ChainScannerService(
    ChainScanner scanner,
    ChainScanState state,
    IOptions<ChainOptions> options,
    TimeProvider clock,
    ILogger<ChainScannerService> logger) : BackgroundService
{
    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The worker must survive any failure (NFR-4); it is logged and retried.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.Value.ChainId, options.Value.Confirmations);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await scanner.RunPassAsync(stoppingToken).ConfigureAwait(false);
                delay = options.Value.PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var failures = state.ReportFailure();
                delay = Backoff.Delay(failures, options.Value.RetryBaseDelay, options.Value.RetryMaxDelay);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Chain scanner started (chain {ChainId}, {Confirmations} confirmations)")]
    private static partial void LogStarted(ILogger logger, long chainId, int confirmations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scan pass failed ({Failures} in a row); retrying in {DelaySeconds:0.0} s")]
    private static partial void LogPassFailed(ILogger logger, Exception exception, int failures, double delaySeconds);
}

/// <summary>Runs <see cref="InvoiceExpiryProcessor"/> every <see cref="ChainOptions.ExpiryInterval"/>; failures are logged and retried with backoff.</summary>
public sealed partial class InvoiceExpiryService(
    InvoiceExpiryProcessor processor,
    IOptions<ChainOptions> options,
    TimeProvider clock,
    ILogger<InvoiceExpiryService> logger) : BackgroundService
{
    /// <inheritdoc />
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The worker must survive any failure (NFR-4); it is logged and retried.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await processor.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                failures = 0;
                delay = options.Value.ExpiryInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                delay = Backoff.Delay(failures, options.Value.RetryBaseDelay, options.Value.RetryMaxDelay);
                LogFailed(logger, ex, failures, delay.TotalSeconds);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expiry run failed ({Failures} in a row); retrying in {DelaySeconds:0.0} s")]
    private static partial void LogFailed(ILogger logger, Exception exception, int failures, double delaySeconds);
}

/// <summary>Exponential backoff with ±20 % jitter.</summary>
public static class Backoff
{
    /// <summary>Delay before retry number <paramref name="failures"/> (1-based): base·2^(n-1), capped at <paramref name="max"/>, ±20 % jitter.</summary>
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Jitter, not security.")]
    public static TimeSpan Delay(int failures, TimeSpan baseDelay, TimeSpan max)
    {
        var exponent = Math.Clamp(failures - 1, 0, 30);
        var raw = Math.Min(max.TotalMilliseconds, baseDelay.TotalMilliseconds * Math.Pow(2, exponent));
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(Math.Min(max.TotalMilliseconds, raw * jitter));
    }
}

/// <summary>
/// Healthy while scan passes succeed; Degraded when none succeeded for <see cref="ChainOptions.StaleAfter"/>
/// (RPC down, wrong chain). Reports the lag behind the head (NFR-7).
/// </summary>
public sealed class ChainScannerHealthCheck(ChainScanState state, IOptions<ChainOptions> options, TimeProvider clock) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["head_block"] = state.Head ?? -1,
            ["last_scanned_block"] = state.LastScannedBlock ?? -1,
            ["lag_blocks"] = state.LagBlocks ?? -1,
            ["consecutive_failures"] = state.ConsecutiveFailures,
        };
        var lastSuccess = state.LastSuccessAt ?? state.CreatedAt;
        var result = clock.GetUtcNow() - lastSuccess > options.Value.StaleAfter
            ? HealthCheckResult.Degraded("No successful chain scan pass recently.", data: data)
            : HealthCheckResult.Healthy("Chain scanner running.", data);
        return Task.FromResult(result);
    }
}
