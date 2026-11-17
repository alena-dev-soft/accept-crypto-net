using AcceptCrypto.Infrastructure.Chain;
using NBitcoin;
using Nethereum.HdWallet;

namespace AcceptCrypto.Infrastructure.Tests.Chain;

public class XpubDepositAddressDeriverTests
{
    // TEST-ONLY key material. Both mnemonics are public; never send real funds to these addresses.

    /// <summary>BIP39 test-vector mnemonic, used as the local-stack merchant wallet.</summary>
    private const string MerchantTestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>xpub at m/44'/60'/0'/0 of <see cref="MerchantTestMnemonic"/> (the local-stack merchant xpub).</summary>
    private const string MerchantTestXpub =
        "xpub6EF8jXqFeFEW5bwMU7RpQtHkzE4KJxcqJtvkCjJumzW8CPpacXkb92ek4WzLQXjL93HycJwTPUAcuNxCqFPKKU5m5Z2Vq4nCyh5CyPeBFFr";

    /// <summary>Anvil's default mnemonic and its xpub at m/44'/60'/0'/0.</summary>
    private const string AnvilMnemonic = "test test test test test test test test test test test junk";

    private const string AnvilXpub =
        "xpub6DyUKdwoLWmUJ4Tn9Bbsdtx7B5Ws18mEN19e5HT52ikE53FiUheSQXrZUNPovqfyKmw4579A1Mm3GXXKM39N64uooBfJ4tNAzFsEbodRTx4";

    // Reference addresses computed independently with Foundry:
    //   cast wallet address --mnemonic "<mnemonic>" --mnemonic-index <i>   (path m/44'/60'/0'/0/<i>)
    [Theory]
    [InlineData(0L, "0x9858EfFD232B4033E47d90003D41EC34EcaEda94")]
    [InlineData(1L, "0x6Fac4D18c912343BF86fa7049364Dd4E424Ab9C0")]
    [InlineData(2L, "0xb6716976A3ebe8D39aCEB04372f22Ff8e6802D7A")]
    [InlineData(1000L, "0xaC2Dbb7Dc8Fa82F47021e01255fBD767a2Ec4EaD")]
    [InlineData(2147483647L, "0x8848bfC75a28756B521b09afDC120BdDddC7d7c9")]
    public void Merchant_test_xpub_derives_the_reference_addresses(long index, string expected)
    {
        var deriver = new XpubDepositAddressDeriver(MerchantTestXpub);

        Assert.Equal(expected, deriver.DeriveAddress(index)); // exact string: EIP-55 checksum casing included
    }

    [Theory]
    [InlineData(0L, "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266")]
    [InlineData(1L, "0x70997970C51812dc3A010C7d01b50e0d17dc79C8")]
    [InlineData(2L, "0x3C44CdDdB6a900fa2b585dd299e03d12FA4293BC")]
    public void Anvil_xpub_derives_the_well_known_anvil_accounts(long index, string expected)
    {
        Assert.Equal(expected, new XpubDepositAddressDeriver(AnvilXpub).DeriveAddress(index));
    }

    [Fact]
    public void Committed_xpubs_match_their_mnemonics()
    {
        Assert.Equal(MerchantTestXpub, XpubOf(MerchantTestMnemonic, Network.Main));
        Assert.Equal(AnvilXpub, XpubOf(AnvilMnemonic, Network.Main));
    }

    [Fact]
    public void Public_derivation_matches_private_key_derivation_of_the_wallet()
    {
        // The merchant's wallet derives private keys from the seed; the service only has the xpub.
        // Both paths must land on the same address, or funds would go to an address nobody controls.
        var wallet = new Wallet(MerchantTestMnemonic, seedPassword: null);
        var deriver = new XpubDepositAddressDeriver(MerchantTestXpub);

        foreach (var index in new[] { 0, 3, 7, 42, 99, 12345 })
        {
            Assert.Equal(wallet.GetAccount(index).Address, deriver.DeriveAddress(index));
        }
    }

    [Fact]
    public void Derivation_is_deterministic_across_calls_and_instances()
    {
        var first = new XpubDepositAddressDeriver(MerchantTestXpub);
        var second = new XpubDepositAddressDeriver(" " + MerchantTestXpub + "\n");

        var addresses = Enumerable.Range(0, 50).Select(i => first.DeriveAddress(i)).ToList();

        Assert.Equal(addresses, Enumerable.Range(0, 50).Select(i => second.DeriveAddress(i)));
        Assert.Equal(addresses, Enumerable.Range(0, 50).Select(i => first.DeriveAddress(i)));
        Assert.Equal(50, addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Testnet_serialised_key_tpub_gives_the_same_addresses()
    {
        var tpub = XpubOf(MerchantTestMnemonic, Network.TestNet);
        Assert.StartsWith("tpub", tpub, StringComparison.Ordinal);

        Assert.Equal(
            new XpubDepositAddressDeriver(MerchantTestXpub).DeriveAddress(5),
            new XpubDepositAddressDeriver(tpub).DeriveAddress(5));
    }

    [Fact]
    public void Reports_the_depth_of_the_configured_key()
    {
        Assert.Equal(4, new XpubDepositAddressDeriver(MerchantTestXpub).Depth);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2147483648L)] // first hardened index: impossible without the private key
    [InlineData(long.MaxValue)]
    public void Rejects_indexes_outside_the_non_hardened_range(long index)
    {
        var deriver = new XpubDepositAddressDeriver(MerchantTestXpub);

        Assert.Throws<ArgumentOutOfRangeException>(() => deriver.DeriveAddress(index));
    }

    [Fact]
    public void Rejects_an_extended_private_key_without_echoing_it()
    {
        var xprv = new Mnemonic(MerchantTestMnemonic).DeriveExtKey()
            .Derive(new KeyPath("m/44'/60'/0'/0")).ToString(Network.Main);

        var ex = Assert.Throws<ArgumentException>(() => new XpubDepositAddressDeriver(xprv));

        Assert.Contains("PRIVATE", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(xprv, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-xpub")]
    [InlineData("xpub6EF8jXqFeFEW5bwMU7RpQtHkzE4KJxcqJtvkCjJumzW8CPpacXkb92ek4WzLQXjL93HycJwTPUAcuNxCqFPKKU5m5Z2Vq4nCyh5CyPeBFFs")] // bad checksum
    public void Rejects_missing_or_malformed_keys(string value)
    {
        Assert.Throws<ArgumentException>(() => new XpubDepositAddressDeriver(value));
    }

    private static string XpubOf(string mnemonic, Network network) =>
        new Mnemonic(mnemonic).DeriveExtKey().Derive(new KeyPath("m/44'/60'/0'/0")).Neuter().ToString(network);
}
