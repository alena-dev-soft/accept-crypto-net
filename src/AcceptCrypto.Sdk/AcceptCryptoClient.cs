using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AcceptCrypto.Sdk.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Sdk;

/// <summary>Default <see cref="IAcceptCryptoClient"/> over <see cref="HttpClient"/> (typed client, FR-23/FR-24).</summary>
public sealed class AcceptCryptoClient : IAcceptCryptoClient
{
    /// <summary>API key header.</summary>
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Idempotency key header.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Response header set when a create request was replayed.</summary>
    public const string ReplayedHeader = "Idempotent-Replayed";

    private readonly HttpClient _http;
    private readonly Func<AcceptCryptoOptions> _options;

    /// <summary>Creates a client without dependency injection.</summary>
    /// <param name="httpClient">The HTTP client; its <see cref="HttpClient.BaseAddress"/> defaults to <see cref="AcceptCryptoOptions.BaseUrl"/>.</param>
    /// <param name="options">Options (BaseUrl, ApiKey).</param>
    public AcceptCryptoClient(HttpClient httpClient, AcceptCryptoOptions options)
        : this(httpClient, () => options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (httpClient.BaseAddress is null && options.BaseUrl is not null)
        {
            httpClient.BaseAddress = WithTrailingSlash(options.BaseUrl);
        }
    }

    /// <summary>Constructor used by dependency injection (typed HttpClient).</summary>
    [ActivatorUtilitiesConstructor]
    public AcceptCryptoClient(HttpClient httpClient, IOptionsMonitor<AcceptCryptoOptions> options)
        : this(httpClient, () => options.CurrentValue)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    private AcceptCryptoClient(HttpClient httpClient, Func<AcceptCryptoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<CreatedInvoice> CreateInvoiceAsync(
        CreateInvoiceRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExternalOrderId, nameof(request));

        long? expiresInSeconds = null;
        if (request.ExpiresIn is { } lifetime)
        {
            if (lifetime <= TimeSpan.Zero || lifetime.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                throw new ArgumentException("ExpiresIn must be a positive whole number of seconds.", nameof(request));
            }

            expiresInSeconds = (long)lifetime.TotalSeconds;
        }

        idempotencyKey ??= _options().GenerateIdempotencyKeys ? Guid.NewGuid().ToString() : null;
        if (idempotencyKey is not null && !IsValidIdempotencyKey(idempotencyKey))
        {
            throw new ArgumentException("An idempotency key has 1–255 printable ASCII characters.", nameof(idempotencyKey));
        }

        var body = new CreateInvoiceWire(request.Amount.ToString(), request.ExternalOrderId, request.Metadata, expiresInSeconds);
        using var message = NewRequest(HttpMethod.Post, "v1/invoices");
        message.Content = JsonContent.Create(body, options: AcceptCryptoJson.Options);
        if (idempotencyKey is not null)
        {
            message.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey);
        }

        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        var invoice = await ReadAsync<CreatedInvoice>(response, cancellationToken).ConfigureAwait(false);
        var replayed = response.Headers.TryGetValues(ReplayedHeader, out var values)
                       && values.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
        return invoice with { IdempotentReplayed = replayed };
    }

    /// <inheritdoc />
    public Task<InvoiceDetails> GetInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        GetAsync<InvoiceDetails>($"v1/invoices/{invoiceId}", cancellationToken);

    /// <inheritdoc />
    public Task<Page<Invoice>> ListInvoicesAsync(ListInvoicesQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ListInvoicesQuery();
        var path = "v1/invoices" + QueryString(
            ("status", query.Status?.ToWireName()),
            ("created_from", query.CreatedFrom?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("created_to", query.CreatedTo?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("limit", query.Limit?.ToString(CultureInfo.InvariantCulture)),
            ("cursor", query.Cursor));
        return GetAsync<Page<Invoice>>(path, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Invoice> ListAllInvoicesAsync(
        ListInvoicesQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query ??= new ListInvoicesQuery();
        while (true)
        {
            var page = await ListInvoicesAsync(query, cancellationToken).ConfigureAwait(false);
            foreach (var invoice in page.Data)
            {
                yield return invoice;
            }

            if (page.NextCursor is null)
            {
                yield break;
            }

            query = query with { Cursor = page.NextCursor };
        }
    }

    /// <inheritdoc />
    public async Task<Invoice> CancelInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        using var message = NewRequest(HttpMethod.Post, $"v1/invoices/{invoiceId}/cancel");
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<Invoice>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Page<UnmatchedPayment>> ListUnmatchedPaymentsAsync(
        ListUnmatchedPaymentsQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ListUnmatchedPaymentsQuery();
        var path = "v1/unmatched-payments" + QueryString(
            ("limit", query.Limit?.ToString(CultureInfo.InvariantCulture)),
            ("cursor", query.Cursor));
        return GetAsync<Page<UnmatchedPayment>>(path, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<UnmatchedPayment> ListAllUnmatchedPaymentsAsync(
        ListUnmatchedPaymentsQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query ??= new ListUnmatchedPaymentsQuery();
        while (true)
        {
            var page = await ListUnmatchedPaymentsAsync(query, cancellationToken).ConfigureAwait(false);
            foreach (var payment in page.Data)
            {
                yield return payment;
            }

            if (page.NextCursor is null)
            {
                yield break;
            }

            query = query with { Cursor = page.NextCursor };
        }
    }

    /// <inheritdoc />
    public async Task<WebhookEventInfo> ResendEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        using var message = NewRequest(HttpMethod.Post, $"v1/events/{eventId}/resend");
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<WebhookEventInfo>(response, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsValidIdempotencyKey(string key) =>
        key.Length is >= 1 and <= 255 && key.All(c => c is >= '!' and <= '~');

    private static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private static string QueryString(params (string Name, string? Value)[] parameters)
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(builder.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var message = NewRequest(HttpMethod.Get, path);
        using var response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var options = _options();
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            throw new InvalidOperationException("AcceptCrypto:ApiKey is not configured.");
        }

        Uri uri;
        if (_http.BaseAddress is not null)
        {
            uri = new Uri(path, UriKind.Relative);
        }
        else if (options.BaseUrl is not null)
        {
            uri = new Uri(WithTrailingSlash(options.BaseUrl), path);
        }
        else
        {
            throw new InvalidOperationException("AcceptCrypto:BaseUrl is not configured.");
        }

        var message = new HttpRequestMessage(method, uri);
        message.Headers.TryAddWithoutValidation(ApiKeyHeader, options.ApiKey);
        message.Headers.Accept.ParseAdd("application/json");
        return message;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AcceptCryptoConnectionException($"AcceptCrypto service unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcceptCryptoConnectionException("AcceptCrypto service did not answer in time.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The status code alone still makes a useful exception.
            }

            throw ApiProblem.Parse(response.StatusCode, body).ToException();
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var result = await response.Content.ReadFromJsonAsync<T>(AcceptCryptoJson.Options, cancellationToken).ConfigureAwait(false);
            return result ?? throw new AcceptCryptoException($"AcceptCrypto service returned an empty {typeof(T).Name} (HTTP {(int)response.StatusCode}).");
        }
        catch (JsonException ex)
        {
            throw new AcceptCryptoException($"AcceptCrypto service returned an unexpected {typeof(T).Name} payload: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AcceptCryptoConnectionException($"Reading the AcceptCrypto response failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcceptCryptoConnectionException("AcceptCrypto service did not answer in time.", ex);
        }
    }

    private sealed record CreateInvoiceWire(
        string Amount,
        string ExternalOrderId,
        IReadOnlyDictionary<string, string>? Metadata,
        long? ExpiresInSeconds);
}
