using System.Net.Http.Headers;
using System.Text.Json;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Payer;

/// <summary>What the payer needs to pay an invoice (FR-4).</summary>
internal sealed record PaymentDetails(
    string Address,
    UsdcAmount Amount,
    string? TokenAddress,
    long? ChainId,
    string? Status,
    DateTimeOffset? ExpiresAt);

/// <summary>How an invoice argument resolves to an HTTP request.</summary>
internal sealed record InvoiceReference(Uri ServiceBaseUrl, string Value, bool IsInvoiceId)
{
    /// <summary>Relative path of the endpoint that returns the payment details.</summary>
    public string Path => IsInvoiceId
        ? $"v1/invoices/{Uri.EscapeDataString(Value)}"
        : $"api/public/invoices/{Uri.EscapeDataString(Value)}";

    /// <summary>
    /// Accepts a payment-page URL (<c>http://host/pay/{token}</c>: service URL taken from it), an invoice ID (GUID,
    /// merchant API, needs the API key) or a public payment-page token. With <paramref name="serviceOverride"/> (an
    /// explicit <c>--service</c> / <c>ACCEPTCRYPTO_SERVICE_URL</c>), a URL only contributes its token: inside Docker the
    /// page URL says <c>localhost:5052</c>, while the service is reachable as <c>service:8080</c>.
    /// </summary>
    public static InvoiceReference Parse(string argument, Uri defaultServiceUrl, Uri? serviceOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument);
        if (Uri.TryCreate(argument, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !string.Equals(segments[^2], "pay", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"Expected a payment page URL like http://host/pay/<token>, got '{argument}'.");
            }

            if (serviceOverride is not null)
            {
                return new InvoiceReference(WithTrailingSlash(serviceOverride), segments[^1], IsInvoiceId: false);
            }

            var prefix = string.Join('/', segments[..^2]);
            var baseUrl = new Uri(url.GetLeftPart(UriPartial.Authority) + "/" + (prefix.Length > 0 ? prefix + "/" : string.Empty));
            return new InvoiceReference(baseUrl, segments[^1], IsInvoiceId: false);
        }

        var value = argument.Trim();
        return new InvoiceReference(WithTrailingSlash(defaultServiceUrl), value, Guid.TryParse(value, out _));
    }

    private static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}

/// <summary>Fetches payment details from the service (public payment-page endpoint or merchant API).</summary>
internal sealed class PaymentDetailsClient(HttpClient http)
{
    public async Task<PaymentDetails> GetAsync(InvoiceReference reference, string? apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(reference.ServiceBaseUrl, reference.Path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (reference.IsInvoiceId)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new InvalidOperationException(
                    "An invoice ID needs the merchant API key (--api-key or ACCEPTCRYPTO_API_KEY). " +
                    "Pass the public payment-page token or URL instead to use the public endpoint.");
            }

            request.Headers.Add("X-Api-Key", apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Cannot reach the AcceptCrypto service at {reference.ServiceBaseUrl} ({ex.Message}). Is it running? Use --service to point elsewhere.", ex);
        }

        using var _ = response;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET {request.RequestUri} returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body)}");
        }

        return Parse(body);
    }

    /// <summary>
    /// Reads the payment details JSON (snake_case). Required: <c>address</c> (or <c>deposit_address</c>) and
    /// <c>amount</c> (decimal string). Optional: <c>token_address</c>, <c>chain_id</c>, <c>status</c>, <c>expires_at</c>.
    /// The fields may also be nested in a <c>payment</c> object.
    /// </summary>
    public static PaymentDetails Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("payment", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            root = nested;
        }

        var address = String(root, "address") ?? String(root, "deposit_address")
            ?? throw new FormatException("Payment details have no 'address'.");
        var amountText = String(root, "amount") ?? throw new FormatException("Payment details have no 'amount'.");
        if (!UsdcAmount.TryParse(amountText, out var amount))
        {
            throw new FormatException($"Payment details amount '{amountText}' is not a USDC amount.");
        }

        long? chainId = root.TryGetProperty("chain_id", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : null;
        DateTimeOffset? expiresAt = String(root, "expires_at") is { } e && DateTimeOffset.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        return new PaymentDetails(address, amount, String(root, "token_address"), chainId, String(root, "status"), expiresAt);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
