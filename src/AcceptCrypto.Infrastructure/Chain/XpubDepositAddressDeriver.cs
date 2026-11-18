using Nethereum.Util;
using NBitcoin;

namespace AcceptCrypto.Infrastructure.Chain;

/// <summary>Derives the deposit address of an invoice from its derivation index (DECISIONS D1).</summary>
public interface IDepositAddressDeriver
{
    /// <summary>Returns the EIP-55 checksummed address of child <paramref name="index"/>.</summary>
    /// <param name="index">BIP32 non-hardened child index, 0 ≤ index &lt; 2^31.</param>
    string DeriveAddress(long index);
}

/// <summary>
/// BIP32 public-key derivation: <c>address(i) = keccak256(uncompressed(xpub / i))[12..32]</c>, EIP-55 checksummed.
/// Works on an extended <b>public</b> key only — the service never sees a private key (NFR-5).
/// </summary>
/// <remarks>
/// The configured xpub must be the key at the wallet's "external chain" level, i.e. <c>m/44'/60'/0'/0</c> for
/// MetaMask/Ledger-style accounts; then <c>xpub / i</c> is the wallet's account <c>m/44'/60'/0'/0/i</c>.
/// Derivation is deterministic and thread-safe (no mutable state).
/// </remarks>
public sealed class XpubDepositAddressDeriver : IDepositAddressDeriver
{
    /// <summary>First hardened index; only indexes below it can be derived from a public key.</summary>
    public const long MaxIndexExclusive = 0x8000_0000L;

    private readonly ExtPubKey _extPubKey;

    /// <summary>Parses the extended public key (Base58Check, <c>xpub…</c> or <c>tpub…</c>).</summary>
    /// <exception cref="ArgumentException">The value is missing, malformed, or an extended private key.</exception>
    public XpubDepositAddressDeriver(string xpub)
    {
        if (string.IsNullOrWhiteSpace(xpub))
        {
            throw new ArgumentException("The merchant xpub is not configured.", nameof(xpub));
        }

        var trimmed = xpub.Trim();

        // Never log or echo the value: if someone pastes an xprv by mistake it must not end up in logs (NFR-5).
        if (trimmed.StartsWith("xprv", StringComparison.Ordinal) || trimmed.StartsWith("tprv", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An extended PRIVATE key was configured. The service only accepts an extended public key (xpub).",
                nameof(xpub));
        }

        _extPubKey = TryParse(trimmed)
            ?? throw new ArgumentException("The merchant xpub is not a valid BIP32 extended public key.", nameof(xpub));
    }

    /// <summary>Depth of the configured key in its BIP32 tree (4 for <c>m/44'/60'/0'/0</c>).</summary>
    public int Depth => _extPubKey.Depth;

    /// <inheritdoc />
    public string DeriveAddress(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxIndexExclusive);

        var child = _extPubKey.Derive((uint)index);
        return ToEthereumAddress(child.PubKey);
    }

    /// <summary>Ethereum address of a secp256k1 public key (EIP-55 checksummed).</summary>
    internal static string ToEthereumAddress(PubKey publicKey)
    {
        var uncompressed = publicKey.Decompress().ToBytes(); // 0x04 || X(32) || Y(32)
        var hash = Sha3Keccack.Current.CalculateHash(uncompressed.AsSpan(1).ToArray());
        var address = "0x" + Convert.ToHexStringLower(hash, 12, 20);
        return AddressUtil.Current.ConvertToChecksumAddress(address);
    }

    private static ExtPubKey? TryParse(string value)
    {
        foreach (var network in new[] { Network.Main, Network.TestNet })
        {
            try
            {
                return ExtPubKey.Parse(value, network);
            }
            catch (FormatException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
