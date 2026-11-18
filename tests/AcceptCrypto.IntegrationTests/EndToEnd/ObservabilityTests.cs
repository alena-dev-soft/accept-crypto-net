using System.Net;
using System.Text.Json;
using AcceptCrypto.IntegrationTests.Chain;
using AcceptCrypto.IntegrationTests.Persistence;
using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;

namespace AcceptCrypto.IntegrationTests.EndToEnd;

/// <summary>
/// NFR-7 on the running system: Prometheus metrics (chain lag, webhook queue, delivery outcomes) and health checks.
/// Runs alone (<see cref="ObservabilityTestGroup"/>): OpenTelemetry listens to meters by name process-wide, so service
/// hosts of tests running in parallel would add their numbers to the scraped values.
/// </summary>
[Collection(ObservabilityTestGroup.Name)]
public sealed class ObservabilityTests(AnvilFixture chain, PostgresFixture postgres)
{
    [Fact]
    public async Task Metrics_endpoint_reports_chain_lag_webhook_queue_and_delivery_outcomes()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres, merchantUp: false);
        var invoice = await h.CreateInvoiceAsync("10");
        await h.PayAsync(invoice, "10");
        await h.WaitForStatusAsync(invoice.Id, InvoiceStatus.Paid, mine: true);

        // Merchant down: failed attempts and a non-empty queue are visible.
        var down = await Eventually.ValueAsync(async () =>
        {
            var text = await ScrapeAsync(h);
            return Value(text, "acceptcrypto_webhooks_queue_length") >= 2 && Value(text, "acceptcrypto_webhooks_attempts_total{", "outcome=\"retry\"") >= 2 ? text : null;
        }, () => "metrics with a pending queue and retries");
        Assert.Contains("# TYPE acceptcrypto_scanner_lag_blocks gauge", down, StringComparison.Ordinal);
        Assert.True(Value(down, "acceptcrypto_scanner_head_block") >= 3);
        Assert.True(Value(down, "acceptcrypto_scanner_last_scanned_block") >= 1);
        Assert.True(Value(down, "acceptcrypto_scanner_transfers_total") >= 1);
        Assert.Contains("acceptcrypto_webhooks_oldest_pending_age_seconds", down, StringComparison.Ordinal);
        Assert.Contains("http_server_request_duration_seconds", down, StringComparison.Ordinal); // built-in ASP.NET Core meter

        // Merchant back: delivered, queue drained, lag back to (near) zero.
        await h.Merchant.StartAsync();
        await h.WaitUntilAllDeliveredAsync(invoice.Id);
        var up = await Eventually.ValueAsync(async () =>
        {
            var text = await ScrapeAsync(h);
            return Value(text, "acceptcrypto_webhooks_queue_length") == 0 && Value(text, "acceptcrypto_webhooks_attempts_total{", "outcome=\"delivered\"") >= 2 ? text : null;
        }, () => "metrics with a drained queue");
        Assert.True(Value(up, "acceptcrypto_scanner_lag_blocks") <= 1);
        Assert.Equal(0, Value(up, "acceptcrypto_webhooks_failed"));
    }

    [Fact]
    public async Task Health_reports_database_rpc_scanner_and_webhook_queue()
    {
        await using var h = await EndToEndHarness.StartAsync(chain, postgres);
        var invoice = await h.CreateInvoiceAsync("1");
        await h.PayAsync(invoice, "1");
        await h.WaitForWebhookAsync(invoice.Id, AcceptCryptoEventTypes.InvoiceConfirming);

        Assert.Equal("Healthy", await h.Http.GetStringAsync(new Uri("/health", UriKind.Relative)));

        using var ready = await h.Http.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        var text = json.RootElement.ToString();
        foreach (var check in new[] { "database", "rpc", "chain-scanner", "webhooks" })
        {
            Assert.Contains($"\"{check}\"", text, StringComparison.Ordinal);
        }

        Assert.Contains("lag_blocks", text, StringComparison.Ordinal);
        Assert.Contains("oldest_pending_age_seconds", text, StringComparison.Ordinal);
    }

    private static async Task<string> ScrapeAsync(EndToEndHarness h)
    {
        using var response = await h.Http.GetAsync(new Uri("/metrics", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // no API key needed, like /health
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Sum of the samples of a metric (optionally only lines containing <paramref name="labelFilter"/>); -1 if absent.</summary>
    private static double Value(string exposition, string metricPrefix, string? labelFilter = null)
    {
        var samples = exposition.Split('\n')
            .Where(l => !l.StartsWith('#') && l.StartsWith(metricPrefix, StringComparison.Ordinal))
            .Where(l => metricPrefix.EndsWith('{') || l.Length > metricPrefix.Length && (l[metricPrefix.Length] is ' ' or '{'))
            .Where(l => labelFilter is null || l.Contains(labelFilter, StringComparison.Ordinal))
            .Select(l => l.Split(' ')[1])
            .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        return samples.Count == 0 ? -1 : samples.Sum();
    }
}

/// <summary>Own Anvil + PostgreSQL, and no other test runs in parallel with this collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ObservabilityTestGroup : ICollectionFixture<AnvilFixture>, ICollectionFixture<PostgresFixture>
{
    public const string Name = "observability";
}
