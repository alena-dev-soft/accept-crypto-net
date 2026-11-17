using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.IntegrationTests.Chain;
using AcceptCrypto.IntegrationTests.Persistence;
using AcceptCrypto.LocalChain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AcceptCrypto.IntegrationTests.Scanner;

/// <summary>The real service host (Program.cs) running the scanner and expiry worker as background services.</summary>
[Collection(AnvilTestGroup.Name)]
public sealed class ScannerHostingTests(AnvilFixture chain, PostgresFixture postgres)
{
    [Fact]
    public async Task Service_detects_and_confirms_a_payment_in_the_background()
    {
        await chain.ResetAsync();
        await using var store = await postgres.CreateStoreAsync();
        await using var factory = ServiceFactory.Create(new()
        {
            ["ConnectionStrings:AcceptCrypto"] = store.ConnectionString,
            ["Chain:Enabled"] = "true",
            ["Chain:RpcUrl"] = chain.RpcUrl,
            ["Chain:TokenAddress"] = AnvilAccounts.MockUsdcAddress,
            ["Chain:Confirmations"] = "2",
            ["Chain:StartBlock"] = "0",
            ["Chain:PollInterval"] = "00:00:00.200",
        });
        using var client = factory.CreateClient(); // starts the host and its background services

        var invoices = factory.Services.GetRequiredService<IInvoiceRepository>();
        var created = await invoices.CreateAsync(null, "fp", i => Invoice.Create(
            new NewInvoice(UsdcAmount.Parse("25"), "order-bg", "0x00000000000000000000000000000000000b0b01", i), new InvoicePolicy(), TimeProvider.System));
        await chain.Usdc.TransferAsync(created.Invoice.DepositAddress, UsdcAmount.Parse("25"));
        await chain.Anvil.MineAsync();

        var status = await WaitForAsync(invoices, created.Invoice.Id, InvoiceStatus.Paid);
        Assert.Equal(InvoiceStatus.Paid, status);

        var health = await client.GetStringAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal("Healthy", health);
    }

    private static async Task<InvoiceStatus?> WaitForAsync(IInvoiceRepository invoices, Guid id, InvoiceStatus expected)
    {
        InvoiceStatus? status = null;
        for (var i = 0; i < 100 && status != expected; i++)
        {
            await Task.Delay(200);
            status = (await invoices.GetAsync(id))?.Status;
        }

        return status;
    }
}

/// <summary>NFR-4: an unreachable RPC node does not crash the service; passes are retried and health degrades.</summary>
public sealed class ScannerResilienceTests
{
    [Fact]
    public async Task Unreachable_rpc_is_retried_with_backoff_and_the_service_keeps_running()
    {
        await using var factory = ServiceFactory.Create(new()
        {
            ["Chain:Enabled"] = "true",
            ["Chain:RpcUrl"] = "http://127.0.0.1:9", // discard port: connection refused
            ["Chain:TokenAddress"] = AnvilAccounts.MockUsdcAddress,
            ["Chain:RetryBaseDelay"] = "00:00:00.020",
            ["Chain:RetryMaxDelay"] = "00:00:00.100",
            ["Chain:RpcTimeout"] = "00:00:02",
            ["Chain:StaleAfter"] = "00:00:00.050",
        });
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ChainScanState>();

        for (var i = 0; i < 100 && state.ConsecutiveFailures < 3; i++)
        {
            await Task.Delay(100);
        }

        Assert.True(state.ConsecutiveFailures >= 3, $"failures: {state.ConsecutiveFailures}");
        var scanner = factory.Services.GetServices<IHostedService>().OfType<ChainScannerService>().Single();
        Assert.False(scanner.ExecuteTask!.IsCompleted); // still looping
        var health = await client.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.True(health.IsSuccessStatusCode);
        Assert.Equal("Degraded", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Backoff_doubles_up_to_the_cap_with_bounded_jitter()
    {
        var b = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(60);
        for (var n = 1; n <= 10; n++)
        {
            var d = Backoff.Delay(n, b, max);
            var nominal = Math.Min(60, Math.Pow(2, n - 1));
            Assert.InRange(d.TotalSeconds, nominal * 0.8 - 1e-9, Math.Min(60, nominal * 1.2) + 1e-9);
        }

        Assert.True(Backoff.Delay(1000, b, max) <= max);
    }
}

internal static class ServiceFactory
{
    public static WebApplicationFactory<Program> Create(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (key, value) in Api.TestMerchant.Settings().Concat(settings))
            {
                b.UseSetting(key, value);
            }

            b.UseSetting("Logging:LogLevel:Default", "Warning");
        });
}
