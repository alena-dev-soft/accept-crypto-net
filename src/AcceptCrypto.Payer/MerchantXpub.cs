using NBitcoin;
using Nethereum.HdWallet;

namespace AcceptCrypto.Payer;

/// <summary>
/// Derives the merchant's extended public key for <c>Merchant:Xpub</c> from a BIP39 mnemonic (TESTNET wallets only).
/// The path is the one the service expects (DECISIONS.md D13): <c>m/44'/60'/0'/0</c>; deposit address <i>i</i> is
/// <c>m/44'/60'/0'/0/i</c> = MetaMask "Account i+1" when the same mnemonic is imported there.
/// </summary>
internal static class MerchantXpub
{
    public const string AccountPath = "m/44'/60'/0'/0";

    /// <summary>Public BIP39 mnemonics used by the local stack and the tests: anyone can spend from them.</summary>
    private static readonly string[] PublicMnemonics =
    [
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
        "test test test test test test test test test test test junk",
    ];

    /// <summary>Generates a fresh 12-word English mnemonic.</summary>
    public static string NewMnemonic() => new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

    /// <summary>xpub at <see cref="AccountPath"/> and the first <paramref name="addresses"/> deposit addresses.</summary>
    /// <exception cref="ArgumentException">Invalid or public mnemonic (the words are never echoed).</exception>
    public static (string Xpub, IReadOnlyList<string> Addresses) Derive(string mnemonic, int addresses = 3)
    {
        var words = Normalize(mnemonic);
        if (PublicMnemonics.Contains(words, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "This is a PUBLIC test mnemonic (anyone can spend from it). Generate your own with 'pay xpub new'.");
        }

        Mnemonic parsed;
        try
        {
            parsed = new Mnemonic(words, Wordlist.English);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("MERCHANT_MNEMONIC is not a valid English BIP39 mnemonic.", ex);
        }

        if (!parsed.IsValidChecksum)
        {
            throw new ArgumentException("MERCHANT_MNEMONIC has an invalid BIP39 checksum (check the words and their order).");
        }

        var xpub = parsed.DeriveExtKey().Derive(new KeyPath(AccountPath)).Neuter().ToString(Network.Main);
        var wallet = new Wallet(words, seedPassword: null);
        var list = Enumerable.Range(0, addresses).Select(i => wallet.GetAccount(i).Address).ToList();
        return (xpub, list);
    }

    private static string Normalize(string mnemonic) =>
        string.Join(' ', mnemonic.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
