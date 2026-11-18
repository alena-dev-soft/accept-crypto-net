using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>
/// Webhook signing (FR-13): <c>hex(HMAC-SHA256(secret, "{timestamp}.{body}"))</c> over the exact UTF-8 bytes of the
/// body, where <c>timestamp</c> is Unix seconds of the delivery attempt. Wire format: docs/webhooks.md.
/// </summary>
public static class WebhookSignature
{
    /// <summary>Header with the event ID (FR-16): the same on every retry and resend.</summary>
    public const string EventIdHeader = "AcceptCrypto-Event-Id";

    /// <summary>Header with the event type, e.g. <c>invoice.paid</c> (informational; the body is authoritative).</summary>
    public const string EventTypeHeader = "AcceptCrypto-Event-Type";

    /// <summary>Header with the signing timestamp, Unix seconds (new on every attempt).</summary>
    public const string TimestampHeader = "AcceptCrypto-Timestamp";

    /// <summary>Header with the signature(s): <c>v1=&lt;hex&gt;</c>, comma-separated when more than one.</summary>
    public const string SignatureHeader = "AcceptCrypto-Signature";

    /// <summary>Scheme prefix of a signature entry.</summary>
    public const string Scheme = "v1";

    /// <summary>Computes the lowercase-hex HMAC-SHA256 of <c>{timestamp}.{body}</c>.</summary>
    public static string Compute(string secret, long timestamp, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(body);
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var message = new byte[prefix.Length + bodyBytes.Length];
        prefix.CopyTo(message, 0);
        bodyBytes.CopyTo(message, prefix.Length);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>
    /// The <see cref="SignatureHeader"/> value: one <c>v1=</c> entry per secret, current secret first.
    /// </summary>
    public static string CreateHeader(IEnumerable<string> secrets, long timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var entries = secrets.Select(s => Scheme + "=" + Compute(s, timestamp, body)).ToList();
        if (entries.Count == 0)
        {
            throw new ArgumentException("At least one secret is required.", nameof(secrets));
        }

        return string.Join(',', entries);
    }

    /// <summary>
    /// Verifies a signature header against any of <paramref name="secrets"/> in constant time per comparison.
    /// The service uses it in tests; merchants use the SDK's verifier, which also enforces the timestamp tolerance.
    /// </summary>
    public static bool Verify(IEnumerable<string> secrets, long timestamp, string body, string signatureHeader)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(signatureHeader);
        var candidates = signatureHeader.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.StartsWith(Scheme + "=", StringComparison.Ordinal))
            .Select(e => Encoding.ASCII.GetBytes(e[(Scheme.Length + 1)..]))
            .ToList();
        var valid = false;
        foreach (var secret in secrets)
        {
            var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp, body));
            foreach (var candidate in candidates)
            {
                valid |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        return valid;
    }
}
