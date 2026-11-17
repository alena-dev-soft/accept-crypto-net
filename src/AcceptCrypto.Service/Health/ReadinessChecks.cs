using System.Text.Json;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.Health;

/// <summary>PostgreSQL reachable (readiness, NFR-7). The exception text is not exposed: it may contain host names.</summary>
internal sealed class DatabaseHealthCheck(IDbContextFactory<AcceptCryptoDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var db = await factory.CreateDbContextAsync(timeout.Token).ConfigureAwait(false);
            return await db.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false)
                ? HealthCheckResult.Healthy("Database reachable.")
                : new HealthCheckResult(context.Registration.FailureStatus, "Database not reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Database not reachable.");
        }
    }
}

/// <summary>JSON-RPC node reachable and on the configured chain (readiness, only when the scanner is enabled).</summary>
internal sealed class RpcHealthCheck(IChainRpcClient rpc, IOptionsMonitor<ChainOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var chainId = await rpc.GetChainIdAsync(timeout.Token).ConfigureAwait(false);
            if (chainId != options.CurrentValue.ChainId)
            {
                return new HealthCheckResult(context.Registration.FailureStatus, "RPC node is on a different chain.");
            }

            var head = await rpc.GetHeadBlockNumberAsync(timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("RPC reachable.", new Dictionary<string, object> { ["head_block"] = head });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // ChainRpcException messages never contain the RPC URL (it may hold a provider key); still keep it generic.
            return new HealthCheckResult(context.Registration.FailureStatus, "RPC node not reachable.");
        }
    }
}

/// <summary>Writes a health report as JSON: overall status plus status/description per check, no exception details.</summary>
internal static class HealthReportWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), description = e.Value.Description, data = e.Value.Data }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body), context.RequestAborted);
    }
}
