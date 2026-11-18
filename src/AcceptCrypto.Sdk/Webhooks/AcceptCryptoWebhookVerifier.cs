using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AcceptCrypto.Sdk.Webhooks;

/// <summary>Why a webhook was rejected.</summary>
public enum WebhookRejectionReason
{
    /// <summary><c>AcceptCrypto-Timestamp</c> or <c>AcceptCrypto-Signature</c> is missing.</summary>
    MissingHeaders = 1,

    /// <summary>The timestamp is not a Unix time in seconds.</summary>
    InvalidTimestamp,

    /// <summary>The timestamp is outside the tolerance window (possible replay).</summary>
    TimestampOutsideTolerance,

    /// <summary>No <c>v1=</c> signature matches any configured secret (forged or altered).</summary>
    SignatureMismatch,

    /// <summary>The body is not a valid event.</summary>
    MalformedPayload,

    /// <summary>The body exceeds the configured size limit.</summary>
    PayloadTooLarge,
}

/// <summary>A webhook failed verification or parsing (FR-26).</summary>
public sealed class AcceptCryptoWebhookException : AcceptCryptoException
{
    /// <inheritdoc />
    public AcceptCryptoWebhookException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoWebhookException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoWebhookException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a reason.</summary>
    public AcceptCryptoWebhookException(WebhookRejectionReason reason, string message, Exception? innerException = null)
        : base(message, innerException!) => Reason = reason;

    /// <summary>Why the webhook was rejected.</summary>
    public WebhookRejectionReason Reason { get; }
}

/// <summary>
/// Verifies AcceptCrypto.NET webhook signatures (docs/webhooks.md §2): HMAC-SHA256 over <c>{timestamp}.{raw body}</c>,
/// compared in constant time against every <c>v1=</c> entry with every configured secret (rotation), and a timestamp
/// tolerance window against replays. Usable without ASP.NET Core.
/// </summary>
public sealed class AcceptCryptoWebhookVerifier
{
    /// <summary>Unix timestamp of the delivery attempt.</summary>
    public const string TimestampHeader = "AcceptCrypto-Timestamp";

    /// <summary>Signatures: <c>v1=&lt;hex&gt;[,v1=&lt;hex&gt;]</c>.</summary>
    public const string SignatureHeader = "AcceptCrypto-Signature";

    /// <summary>Event ID (equals <c>id</c> in the body).</summary>
    public const string EventIdHeader = "AcceptCrypto-Event-Id";

    /// <summary>Event type (informational).</summary>
    public const string EventTypeHeader = "AcceptCrypto-Event-Type";

    /// <summary>Default tolerance: 5 minutes.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    private const string Scheme = "v1";
    private const int SignatureBytes = 32;

    private readonly byte[][] _keys;
    private readonly TimeSpan _tolerance;
    private readonly TimeProvider _time;

    /// <summary>Creates a verifier.</summary>
    /// <param name="secrets">Accepted secrets (current first; add the old one during rotation). At least one.</param>
    /// <param name="tolerance">Allowed clock difference; default 5 minutes.</param>
    /// <param name="timeProvider">Clock; default system clock.</param>
    public AcceptCryptoWebhookVerifier(IEnumerable<string> secrets, TimeSpan? tolerance = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _keys = secrets.Where(s => !string.IsNullOrEmpty(s)).Select(Encoding.UTF8.GetBytes).ToArray();
        if (_keys.Length == 0)
        {
            throw new ArgumentException("At least one webhook secret is required (AcceptCrypto:WebhookSecret).", nameof(secrets));
        }

        _tolerance = tolerance ?? DefaultTolerance;
        if (_tolerance <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "The tolerance must be positive.");
        }

        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Computes the <c>v1</c> signature (64 lowercase hex characters) — e.g. to sign test webhooks.</summary>
    public static string ComputeSignature(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(Hmac(Encoding.UTF8.GetBytes(secret), timestamp, body));
    }

    /// <summary>Builds the <c>AcceptCrypto-Signature</c> header value for the given secrets (test helper).</summary>
    public static string ComputeSignatureHeader(IEnumerable<string> secrets, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var parts = new List<string>();
        foreach (var secret in secrets)
        {
            parts.Add($"{Scheme}={ComputeSignature(secret, timestamp, body)}");
        }

        return string.Join(',', parts);
    }

    /// <summary>Checks headers and signature; returns null when the webhook is authentic, otherwise the reason.</summary>
    public WebhookRejectionReason? Verify(string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrWhiteSpace(timestampHeader) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return WebhookRejectionReason.MissingHeaders;
        }

        var ts = timestampHeader.Trim();
        if (ts.Length > 12 || !ts.All(char.IsAsciiDigit) || !long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return WebhookRejectionReason.InvalidTimestamp;
        }

        var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (age.Duration() > _tolerance)
        {
            return WebhookRejectionReason.TimestampOutsideTolerance;
        }

        var candidates = ParseSignatures(signatureHeader);
        if (candidates.Count == 0)
        {
            return WebhookRejectionReason.SignatureMismatch;
        }

        // Compare every pair without early exit, so the time does not depend on which pair matched.
        var match = false;
        foreach (var key in _keys)
        {
            var expected = Hmac(key, timestamp, body);
            foreach (var candidate in candidates)
            {
                match |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        return match ? null : WebhookRejectionReason.SignatureMismatch;
    }

    /// <summary>Verifies the webhook and parses it into a typed event (FR-26/FR-27).</summary>
    /// <exception cref="AcceptCryptoWebhookException">Rejected; see <see cref="AcceptCryptoWebhookException.Reason"/>.</exception>
    public AcceptCryptoEvent VerifyAndParse(string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> body)
    {
        if (Verify(timestampHeader, signatureHeader, body) is { } reason)
        {
            throw new AcceptCryptoWebhookException(reason, Describe(reason));
        }

        return AcceptCryptoEvent.Parse(body);
    }

    /// <summary>A short, secret-free description of a rejection reason.</summary>
    public static string Describe(WebhookRejectionReason reason) => reason switch
    {
        WebhookRejectionReason.MissingHeaders => $"Missing {TimestampHeader} or {SignatureHeader} header.",
        WebhookRejectionReason.InvalidTimestamp => $"{TimestampHeader} is not a Unix time in seconds.",
        WebhookRejectionReason.TimestampOutsideTolerance => $"{TimestampHeader} is outside the tolerance window.",
        WebhookRejectionReason.SignatureMismatch => "No valid v1 signature.",
        WebhookRejectionReason.MalformedPayload => "The body is not a valid event.",
        WebhookRejectionReason.PayloadTooLarge => "The body is too large.",
        _ => "Rejected.",
    };

    private static List<byte[]> ParseSignatures(string header)
    {
        var result = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !part.AsSpan(0, eq).Trim().SequenceEqual(Scheme))
            {
                continue; // Unknown schemes are ignored (docs/webhooks.md §1).
            }

            var hex = part[(eq + 1)..].Trim();
            if (hex.Length != SignatureBytes * 2)
            {
                continue;
            }

            try
            {
                result.Add(Convert.FromHexString(hex));
            }
            catch (FormatException)
            {
                // Not hex: cannot match.
            }

            if (result.Count >= 8)
            {
                break; // Bound the work per request.
            }
        }

        return result;
    }

    private static byte[] Hmac(byte[] key, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(key, message);
    }
}
