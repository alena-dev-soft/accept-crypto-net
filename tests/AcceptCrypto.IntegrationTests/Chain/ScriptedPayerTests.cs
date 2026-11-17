using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain;
using AcceptCrypto.LocalChain;
using AcceptCrypto.Payer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AcceptCrypto.IntegrationTests.Chain;

/// <summary>
/// The scripted payer (<c>pay</c>) end to end against Anvil and a stub of the service's public payment-details
/// endpoint. The invoice address is derived from the TEST-ONLY merchant xpub, as the service will do.
/// </summary>
[Collection(AnvilTestGroup.Name)]
public sealed class ScriptedPayerTests(AnvilFixture chain) : IAsyncLifetime
{
    // TEST ONLY: xpub (m/44'/60'/0'/0) of the public BIP39 test mnemonic "abandon … about".
    private const string MerchantTestXpub =
        "xpub6EF8jXqFeFEW5bwMU7RpQtHkzE4KJxcqJtvkCjJumzW8CPpacXkb92ek4WzLQXjL93HycJwTPUAcuNxCqFPKKU5m5Z2Vq4nCyh5CyPeBFFr";

    private const string Token = "tok_test_7f3a";
    private readonly string _depositAddress = new XpubDepositAddressDeriver(MerchantTestXpub).DeriveAddress(7);
    private WebApplication? _service;
    private long _serviceChainId = AnvilAccounts.ChainId;
    private Uri _serviceUrl = null!;

    public async Task InitializeAsync()
    {
        await chain.ResetAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _service = builder.Build();
        _service.MapGet("/api/public/invoices/{token}", (string token) => token == Token
            ? Results.Json(new Dictionary<string, object>
            {
                ["network"] = "anvil",
                ["chain_id"] = _serviceChainId,
                ["token_symbol"] = "USDC",
                ["token_address"] = AnvilAccounts.MockUsdcAddress,
                ["address"] = _depositAddress,
                ["amount"] = "25.5",
                ["expires_at"] = "2030-01-01T00:00:00Z",
                ["status"] = "pending",
            })
            : Results.NotFound());
        await _service.StartAsync();
        var address = _service.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _serviceUrl = new Uri(address);
    }

    public async Task DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pays_the_exact_invoice_amount_from_the_payment_page_url()
    {
        var (exit, output) = await RunAsync($"{_serviceUrl}pay/{Token}");

        Assert.Equal(0, exit);
        Assert.Contains("pay 25.500000 USDC to " + _depositAddress, output, StringComparison.Ordinal);
        Assert.Equal(UsdcAmount.Parse("25.5"), await chain.Usdc.BalanceOfAsync(_depositAddress));
        Assert.Equal(UsdcAmount.Parse("974.5"), await chain.Usdc.BalanceOfAsync(AnvilAccounts.Payer.Address));
    }

    [Fact]
    public async Task Splits_the_payment_into_parts_that_add_up_exactly()
    {
        var head = await chain.Anvil.GetBlockNumberAsync();

        var (exit, output) = await RunAsync(Token, "--service", _serviceUrl.ToString(), "--split", "3");

        Assert.Equal(0, exit);
        Assert.Contains("[3/3]", output, StringComparison.Ordinal);
        Assert.Equal(head + 3, await chain.Anvil.GetBlockNumberAsync()); // three transactions, three blocks
        Assert.Equal(UsdcAmount.Parse("25.5"), await chain.Usdc.BalanceOfAsync(_depositAddress));
    }

    [Theory]
    [InlineData("20")]          // underpayment
    [InlineData("30.000001")]   // overpayment
    public async Task Sends_a_different_amount_with_amount_option(string amount)
    {
        var (exit, _) = await RunAsync(Token, "--service", _serviceUrl.ToString(), "--amount", amount);

        Assert.Equal(0, exit);
        Assert.Equal(UsdcAmount.Parse(amount), await chain.Usdc.BalanceOfAsync(_depositAddress));
    }

    [Fact]
    public async Task Transfers_to_an_arbitrary_address_without_an_invoice()
    {
        const string stranger = "0x000000000000000000000000000000000000dEaD";

        var (exit, _) = await RunAsync("--to", stranger, "--amount", "1.5");

        Assert.Equal(0, exit);
        Assert.Equal(UsdcAmount.Parse("1.5"), await chain.Usdc.BalanceOfAsync(stranger));
    }

    [Fact]
    public async Task Refuses_to_pay_when_the_invoice_is_on_another_chain()
    {
        _serviceChainId = 84532; // Base Sepolia

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(Token, "--service", _serviceUrl.ToString()));

        Assert.Contains("chain 84532", ex.Message, StringComparison.Ordinal);
        Assert.Equal(UsdcAmount.Zero, await chain.Usdc.BalanceOfAsync(_depositAddress));
    }

    [Fact]
    public async Task Unknown_invoice_is_reported_with_the_service_response()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("nope", "--service", _serviceUrl.ToString()));

        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chain_helper_commands_mine_reorg_and_warp()
    {
        await chain.Usdc.TransferAsync(_depositAddress, UsdcAmount.Parse("3"));

        Assert.Equal(0, (await RunAsync("mine", "2")).Exit);
        Assert.Equal(0, (await RunAsync("reorg", "3")).Exit);
        Assert.Equal(UsdcAmount.Zero, await chain.Usdc.BalanceOfAsync(_depositAddress));

        var (exit, output) = await RunAsync("warp", "600");
        Assert.Equal(0, exit);
        Assert.Contains("Chain time moved by 600s", output, StringComparison.Ordinal);
    }

    private async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var environment = new Dictionary<string, string?> { ["ACCEPTCRYPTO_RPC_URL"] = chain.RpcUrl };
        var app = new PayerApp(output, name => environment.GetValueOrDefault(name));
        var exit = await app.RunAsync(CommandLine.Parse(args), CancellationToken.None);
        return (exit, output.ToString());
    }
}
