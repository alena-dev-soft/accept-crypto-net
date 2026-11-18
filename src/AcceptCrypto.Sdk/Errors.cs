using System.Net;
using System.Text.Json;

namespace AcceptCrypto.Sdk;

/// <summary>Base class of every exception the SDK throws for service or transport problems (FR-25).</summary>
public class AcceptCryptoException : Exception
{
    /// <inheritdoc />
    public AcceptCryptoException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The service could not be reached or did not answer in time (network error, timeout).</summary>
public class AcceptCryptoConnectionException : AcceptCryptoException
{
    /// <inheritdoc />
    public AcceptCryptoConnectionException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoConnectionException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Stable machine-readable error codes of the service (the <c>code</c> member of its Problem Details).</summary>
public static class AcceptCryptoErrorCodes
{
    /// <summary>400: request fields are invalid; see <see cref="AcceptCryptoApiException.ValidationErrors"/>.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>400: the body is not valid JSON for the operation.</summary>
    public const string MalformedRequest = "malformed_request";

    /// <summary>401: no API key.</summary>
    public const string ApiKeyMissing = "api_key_missing";

    /// <summary>401: wrong API key.</summary>
    public const string ApiKeyInvalid = "api_key_invalid";

    /// <summary>404: unknown route.</summary>
    public const string NotFound = "not_found";

    /// <summary>404: no such invoice.</summary>
    public const string InvoiceNotFound = "invoice_not_found";

    /// <summary>404: no such webhook event.</summary>
    public const string EventNotFound = "event_not_found";

    /// <summary>409: only pending invoices can be cancelled.</summary>
    public const string InvoiceNotCancellable = "invoice_not_cancellable";

    /// <summary>422: the idempotency key was used for a different request.</summary>
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    /// <summary>409: concurrent update; retry.</summary>
    public const string Conflict = "conflict";

    /// <summary>500: internal error (logged by the service with the trace ID).</summary>
    public const string InternalError = "internal_error";
}

/// <summary>
/// The service answered with an error (RFC 9457 Problem Details, FR-20). Specific cases have subclasses:
/// <see cref="AcceptCryptoValidationException"/>, <see cref="AcceptCryptoAuthenticationException"/>,
/// <see cref="AcceptCryptoNotFoundException"/>, <see cref="AcceptCryptoConflictException"/>,
/// <see cref="AcceptCryptoIdempotencyKeyReusedException"/>.
/// </summary>
public class AcceptCryptoApiException : AcceptCryptoException
{
    /// <inheritdoc />
    public AcceptCryptoApiException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoApiException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AcceptCryptoApiException(ApiProblem problem)
        : base(problem.ToMessage())
    {
        StatusCode = problem.Status;
        ErrorCode = problem.Code;
        Title = problem.Title;
        Detail = problem.Detail;
        ProblemType = problem.Type;
        Instance = problem.Instance;
        TraceId = problem.TraceId;
        ValidationErrors = problem.Errors;
        Extensions = problem.Extensions;
    }

    /// <summary>HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Machine-readable code (see <see cref="AcceptCryptoErrorCodes"/>); null if the response was not Problem Details.</summary>
    public string? ErrorCode { get; }

    /// <summary>Problem title.</summary>
    public string? Title { get; }

    /// <summary>Problem detail.</summary>
    public string? Detail { get; }

    /// <summary>Problem type URI.</summary>
    public string? ProblemType { get; }

    /// <summary>Problem instance (request path).</summary>
    public string? Instance { get; }

    /// <summary>Service trace ID — quote it when reporting a problem.</summary>
    public string? TraceId { get; }

    /// <summary>Field → messages, for validation errors.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; } = new Dictionary<string, string[]>();

    /// <summary>All other Problem Details members.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; } = new Dictionary<string, JsonElement>();
}

/// <summary>400: the request was rejected (<c>validation_failed</c>, <c>malformed_request</c>).</summary>
public class AcceptCryptoValidationException : AcceptCryptoApiException
{
    /// <inheritdoc />
    public AcceptCryptoValidationException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoValidationException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AcceptCryptoValidationException(ApiProblem problem)
        : base(problem)
    {
    }
}

/// <summary>401: the API key is missing or wrong.</summary>
public class AcceptCryptoAuthenticationException : AcceptCryptoApiException
{
    /// <inheritdoc />
    public AcceptCryptoAuthenticationException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoAuthenticationException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AcceptCryptoAuthenticationException(ApiProblem problem)
        : base(problem)
    {
    }
}

/// <summary>404: the invoice or event does not exist.</summary>
public class AcceptCryptoNotFoundException : AcceptCryptoApiException
{
    /// <inheritdoc />
    public AcceptCryptoNotFoundException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoNotFoundException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AcceptCryptoNotFoundException(ApiProblem problem)
        : base(problem)
    {
    }
}

/// <summary>409: the operation conflicts with the current state (e.g. cancelling a paid invoice).</summary>
public class AcceptCryptoConflictException : AcceptCryptoApiException
{
    /// <inheritdoc />
    public AcceptCryptoConflictException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoConflictException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AcceptCryptoConflictException(ApiProblem problem)
        : base(problem) =>
        CurrentStatus = problem.Extensions.TryGetValue("current_status", out var s) && s.ValueKind == JsonValueKind.String
            ? JsonSerializer.Deserialize<InvoiceStatus>(s, Json.AcceptCryptoJson.Options)
            : null;

    /// <summary>For <c>invoice_not_cancellable</c>: the invoice's current status.</summary>
    public InvoiceStatus? CurrentStatus { get; }
}

/// <summary>422: the idempotency key was already used for a different request.</summary>
public class AcceptCryptoIdempotencyKeyReusedException : AcceptCryptoApiException
{
    /// <inheritdoc />
    public AcceptCryptoIdempotencyKeyReusedException()
    {
    }

    /// <inheritdoc />
    public AcceptCryptoIdempotencyKeyReusedException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public AcceptCryptoIdempotencyKeyReusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AcceptCryptoIdempotencyKeyReusedException(ApiProblem problem)
        : base(problem) =>
        InvoiceId = problem.Extensions.TryGetValue("invoice_id", out var id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var g)
            ? g
            : null;

    /// <summary>The invoice that was created with this key.</summary>
    public Guid? InvoiceId { get; }
}

/// <summary>A parsed error response.</summary>
internal sealed record ApiProblem(
    HttpStatusCode Status,
    string? Code,
    string? Title,
    string? Detail,
    string? Type,
    string? Instance,
    string? TraceId,
    IReadOnlyDictionary<string, string[]> Errors,
    IReadOnlyDictionary<string, JsonElement> Extensions)
{
    private static readonly HashSet<string> StandardMembers = ["type", "title", "status", "detail", "instance", "errors", "code", "trace_id", "traceId"];

    public string ToMessage()
    {
        var text = $"AcceptCrypto API error {(int)Status}";
        if (Code is not null)
        {
            text += $" ({Code})";
        }

        text += ": " + (Detail ?? Title ?? Status.ToString());
        if (Errors.Count > 0)
        {
            text += " " + string.Join("; ", Errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"));
        }

        return TraceId is null ? text : $"{text} [trace {TraceId}]";
    }

    public static ApiProblem Parse(HttpStatusCode status, string? body)
    {
        var empty = new ApiProblem(status, null, null, null, null, null, null, new Dictionary<string, string[]>(), new Dictionary<string, JsonElement>());
        if (string.IsNullOrWhiteSpace(body))
        {
            return empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return empty;
            }

            var root = doc.RootElement;
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errorsElement.EnumerateObject())
                {
                    errors[field.Name] = field.Value.ValueKind == JsonValueKind.Array
                        ? field.Value.EnumerateArray().Select(m => m.ToString()).ToArray()
                        : [field.Value.ToString()];
                }
            }

            var extensions = root.EnumerateObject()
                .Where(p => !StandardMembers.Contains(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

            return empty with
            {
                Code = Str(root, "code"),
                Title = Str(root, "title"),
                Detail = Str(root, "detail"),
                Type = Str(root, "type"),
                Instance = Str(root, "instance"),
                TraceId = Str(root, "trace_id") ?? Str(root, "traceId"),
                Errors = errors,
                Extensions = extensions,
            };
        }
        catch (JsonException)
        {
            return empty;
        }
    }

    public AcceptCryptoApiException ToException() => (Status, Code) switch
    {
        (HttpStatusCode.BadRequest, _) => new AcceptCryptoValidationException(this),
        (HttpStatusCode.Unauthorized, _) => new AcceptCryptoAuthenticationException(this),
        (HttpStatusCode.NotFound, _) => new AcceptCryptoNotFoundException(this),
        (HttpStatusCode.Conflict, _) => new AcceptCryptoConflictException(this),
        (HttpStatusCode.UnprocessableEntity, AcceptCryptoErrorCodes.IdempotencyKeyReused) => new AcceptCryptoIdempotencyKeyReusedException(this),
        _ => new AcceptCryptoApiException(this),
    };

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
