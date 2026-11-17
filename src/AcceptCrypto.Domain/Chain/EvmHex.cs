namespace AcceptCrypto.Domain.Chain;

/// <summary>Validation and normalization of EVM hex identifiers (addresses, hashes).</summary>
public static class EvmHex
{
    /// <summary>
    /// Validates a <c>0x</c>-prefixed 20-byte address and returns it in lower case.
    /// Lower case is the comparison form; EIP-55 checksum casing is a presentation concern of the outer layers.
    /// </summary>
    /// <exception cref="DomainException">Malformed address.</exception>
    public static string NormalizeAddress(string address, string paramName) => Normalize(address, 40, paramName);

    /// <summary>Validates a <c>0x</c>-prefixed 32-byte hash (tx or block hash) and returns it in lower case.</summary>
    /// <exception cref="DomainException">Malformed hash.</exception>
    public static string NormalizeHash(string hash, string paramName) => Normalize(hash, 64, paramName);

    private static string Normalize(string? value, int hexDigits, string paramName)
    {
        if (value is null
            || value.Length != hexDigits + 2
            || value[0] != '0'
            || (value[1] != 'x' && value[1] != 'X')
            || !value.AsSpan(2).ContainsOnlyHex())
        {
            throw new DomainException(
                DomainErrorCodes.InvalidChainData,
                $"{paramName} must be 0x followed by {hexDigits} hex digits.");
        }

        return "0x" + value[2..].ToLowerInvariant();
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
