using System.Buffers.Text;
using System.Security.Cryptography;

namespace AcceptCrypto.Domain.Invoices;

/// <summary>Unguessable public token addressing the payment page (FR-29).</summary>
public static class PaymentPageToken
{
    /// <summary>Random bytes per token: 256 bits from a CSPRNG.</summary>
    public const int ByteLength = 32;

    /// <summary>Generates a new token: 32 random bytes, base64url without padding (43 characters).</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));
}
