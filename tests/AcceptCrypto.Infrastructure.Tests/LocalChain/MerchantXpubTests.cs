using AcceptCrypto.Infrastructure.Chain;
using AcceptCrypto.Payer;

namespace AcceptCrypto.Infrastructure.Tests.LocalChain;

/// <summary><c>pay xpub</c>: the merchant xpub for testnet deployments (docs/testnet.md).</summary>
public class MerchantXpubTests
{
    // BIP39 test vector (public, TEST ONLY): never send real funds to its addresses.
    private const string VectorMnemonic = "legal winner thank year wave sausage worth useful legal winner thank yellow";

    [Fact]
    public void Xpub_derives_the_same_addresses_as_the_wallet_and_the_service()
    {
        var (xpub, addresses) = MerchantXpub.Derive(VectorMnemonic, 5);

        Assert.StartsWith("xpub", xpub, StringComparison.Ordinal);
        var deriver = new XpubDepositAddressDeriver(xpub);
        Assert.Equal(4, deriver.Depth); // m/44'/60'/0'/0
        Assert.Equal(addresses, Enumerable.Range(0, 5).Select(i => deriver.DeriveAddress(i)));
    }

    [Fact]
    public void Whitespace_and_case_are_normalised()
    {
        Assert.Equal(
            MerchantXpub.Derive(VectorMnemonic).Xpub,
            MerchantXpub.Derive("  " + VectorMnemonic.ToUpperInvariant().Replace(" ", "\n  ", StringComparison.Ordinal)).Xpub);
    }

    [Theory]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("test test test test test test test test test test test junk")]
    public void Public_test_mnemonics_are_refused(string mnemonic)
    {
        var ex = Assert.Throws<ArgumentException>(() => MerchantXpub.Derive(mnemonic));
        Assert.Contains("PUBLIC", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("legal winner thank year wave sausage worth useful legal winner thank thank")] // bad checksum
    [InlineData("not a mnemonic at all")]
    public void Invalid_mnemonics_are_refused_without_echoing_them(string mnemonic)
    {
        var ex = Assert.Throws<ArgumentException>(() => MerchantXpub.Derive(mnemonic));
        Assert.DoesNotContain("winner", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mnemonic at all", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void New_mnemonics_are_fresh_valid_12_word_phrases()
    {
        var first = MerchantXpub.NewMnemonic();
        var second = MerchantXpub.NewMnemonic();

        Assert.Equal(12, first.Split(' ').Length);
        Assert.NotEqual(first, second);
        Assert.StartsWith("xpub", MerchantXpub.Derive(first).Xpub, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_reads_the_mnemonic_from_the_environment_and_never_prints_it()
    {
        using var output = new StringWriter();
        var app = new PayerApp(output, name => name == "MERCHANT_MNEMONIC" ? VectorMnemonic : null);

        var exit = await app.RunAsync(CommandLine.Parse(["xpub"]), CancellationToken.None);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains(MerchantXpub.Derive(VectorMnemonic).Xpub, text, StringComparison.Ordinal);
        Assert.DoesNotContain("winner", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_without_a_mnemonic_explains_what_to_do()
    {
        var app = new PayerApp(TextWriter.Null, _ => null);

        var ex = await Assert.ThrowsAsync<FormatException>(() => app.RunAsync(CommandLine.Parse(["xpub"]), CancellationToken.None));
        Assert.Contains("MERCHANT_MNEMONIC", ex.Message, StringComparison.Ordinal);
    }
}
