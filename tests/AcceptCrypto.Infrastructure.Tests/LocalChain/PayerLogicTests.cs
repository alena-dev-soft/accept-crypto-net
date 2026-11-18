using System.Text.Json;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.LocalChain;
using AcceptCrypto.Payer;

namespace AcceptCrypto.Infrastructure.Tests.LocalChain;

/// <summary>Pure logic of the local-chain toolkit and the scripted payer (no chain needed).</summary>
public class PayerLogicTests
{
    private static readonly Uri Service = new("http://localhost:5052");

    [Theory]
    [InlineData("10", 1, new[] { "10" })]
    [InlineData("10", 2, new[] { "5", "5" })]
    [InlineData("10.000001", 3, new[] { "3.333334", "3.333334", "3.333333" })]
    [InlineData("0.000003", 3, new[] { "0.000001", "0.000001", "0.000001" })]
    public void Split_adds_up_exactly(string total, int parts, string[] expected)
    {
        var result = PaymentPlan.Split(UsdcAmount.Parse(total), parts);

        Assert.Equal(expected.Select(UsdcAmount.Parse), result);
        Assert.Equal(UsdcAmount.Parse(total), result.Aggregate(UsdcAmount.Zero, (a, b) => a + b));
    }

    [Theory]
    [InlineData("0.000002", 3)]
    [InlineData("1", 0)]
    public void Split_rejects_impossible_splits(string total, int parts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PaymentPlan.Split(UsdcAmount.Parse(total), parts));
    }

    [Fact]
    public void Embedded_artifact_has_the_ERC20_interface_and_bytecode()
    {
        Assert.StartsWith("0x60", MockUsdcArtifact.Bytecode, StringComparison.Ordinal);
        using var abi = JsonDocument.Parse(MockUsdcArtifact.Abi);
        var names = abi.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "transfer", "transferFrom", "approve", "balanceOf", "decimals", "mint", "Transfer" }, names);
    }

    [Fact]
    public void Command_line_parses_invoice_payment_with_options()
    {
        var cmd = CommandLine.Parse(["tok123", "--amount", "12.5", "--split=2"]);

        Assert.Equal("pay", cmd.Command);
        Assert.Equal(["tok123"], cmd.Arguments);
        Assert.Equal("12.5", cmd.Option("--amount"));
        Assert.Equal("2", cmd.Option("--split"));
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("mine")]
    [InlineData("reorg")]
    [InlineData("warp")]
    [InlineData("balance")]
    public void Command_line_recognises_helper_commands(string command)
    {
        Assert.Equal(command, CommandLine.Parse([command, "1"]).Command);
    }

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("tok", "-h")]
    public void Command_line_shows_help(params string[] args)
    {
        Assert.Equal("help", CommandLine.Parse(args).Command);
    }

    [Theory]
    [InlineData("--bogus", "1")]
    [InlineData("tok", "--amount")]
    [InlineData("tok", "--amount", "1", "--amount", "2")]
    public void Command_line_rejects_bad_options(params string[] args)
    {
        Assert.Throws<FormatException>(() => CommandLine.Parse(args));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.0000001")] // 7 decimals
    [InlineData("abc")]
    public void Amount_must_be_positive_with_at_most_6_decimals(string amount)
    {
        Assert.Throws<FormatException>(() => PayerApp.ParseAmount(amount));
    }

    [Fact]
    public void Payment_page_url_gives_service_and_token()
    {
        var reference = InvoiceReference.Parse("https://shop.example:8443/acc/pay/AbC-_123", Service);

        Assert.Equal(new Uri("https://shop.example:8443/acc/"), reference.ServiceBaseUrl);
        Assert.Equal("AbC-_123", reference.Value);
        Assert.False(reference.IsInvoiceId);
        Assert.Equal("api/public/invoices/AbC-_123", reference.Path);
    }

    [Fact]
    public void Explicit_service_url_wins_over_the_host_of_a_payment_page_url()
    {
        var reference = InvoiceReference.Parse("http://localhost:5052/pay/AbC-_123", Service, new Uri("http://service:8080"));

        Assert.Equal(new Uri("http://service:8080/"), reference.ServiceBaseUrl);
        Assert.Equal("api/public/invoices/AbC-_123", reference.Path);
    }

    [Fact]
    public void Token_uses_the_public_endpoint_and_guid_the_merchant_api()
    {
        var token = InvoiceReference.Parse("AbC-_123", Service);
        var id = InvoiceReference.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301", Service);

        Assert.Equal(new Uri("http://localhost:5052/"), token.ServiceBaseUrl);
        Assert.False(token.IsInvoiceId);
        Assert.True(id.IsInvoiceId);
        Assert.Equal("v1/invoices/3f2504e0-4f89-11d3-9a0c-0305e82c3301", id.Path);
    }

    [Fact]
    public void Url_that_is_not_a_payment_page_is_rejected()
    {
        Assert.Throws<FormatException>(() => InvoiceReference.Parse("http://localhost:5052/invoices/x", Service));
    }

    [Fact]
    public void Parses_public_payment_details()
    {
        var details = PaymentDetailsClient.Parse("""
            {"network":"anvil","chain_id":31337,"token_address":"0x5FbDB2315678afecb367f032d93F642f64180aa3",
             "address":"0x9858EfFD232B4033E47d90003D41EC34EcaEda94","amount":"25.5","status":"pending",
             "expires_at":"2030-01-01T00:00:00Z"}
            """);

        Assert.Equal("0x9858EfFD232B4033E47d90003D41EC34EcaEda94", details.Address);
        Assert.Equal(UsdcAmount.Parse("25.5"), details.Amount);
        Assert.Equal(31337, details.ChainId);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), details.ExpiresAt);
    }

    [Fact]
    public void Parses_merchant_api_shape_with_deposit_address_in_a_payment_object()
    {
        var details = PaymentDetailsClient.Parse("""
            {"id":"x","payment":{"deposit_address":"0x9858EfFD232B4033E47d90003D41EC34EcaEda94","amount":"1"}}
            """);

        Assert.Equal(UsdcAmount.Parse("1"), details.Amount);
        Assert.Null(details.ChainId);
        Assert.Null(details.TokenAddress);
    }

    [Theory]
    [InlineData("""{"amount":"1"}""")]
    [InlineData("""{"address":"0x9858EfFD232B4033E47d90003D41EC34EcaEda94"}""")]
    [InlineData("""{"address":"0x9858EfFD232B4033E47d90003D41EC34EcaEda94","amount":"1.1234567"}""")]
    public void Rejects_incomplete_payment_details(string json)
    {
        Assert.Throws<FormatException>(() => PaymentDetailsClient.Parse(json));
    }
}
