using System.Text.Json;
using AcceptCrypto.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AcceptCrypto.Service.Api;

/// <summary>Machine-readable error codes, sent as the <c>code</c> extension of every Problem Details response.</summary>
internal static class ApiErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string MalformedRequest = "malformed_request";
    public const string ApiKeyMissing = "api_key_missing";
    public const string ApiKeyInvalid = "api_key_invalid";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string InvoiceNotFound = "invoice_not_found";
    public const string EventNotFound = "event_not_found";
    public const string InvoiceNotCancellable = "invoice_not_cancellable";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string Conflict = "conflict";
    public const string InternalError = "internal_error";
}

/// <summary>RFC 9457 Problem Details helpers (FR-20).</summary>
internal static class ApiProblems
{
    public const string TypeBase = "https://github.com/alena-dev-soft/accept-crypto-net/blob/main/docs/API.md#";

    public static string TypeFor(string code) => TypeBase + code.Replace('_', '-');

    public static ProblemHttpResult Problem(int status, string code, string title, string detail, IDictionary<string, object?>? extensions = null)
    {
        var ext = new Dictionary<string, object?>(extensions ?? new Dictionary<string, object?>()) { ["code"] = code };
        return TypedResults.Problem(detail: detail, statusCode: status, title: title, type: TypeFor(code), extensions: ext);
    }

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            detail: "One or more request fields are invalid.",
            title: "Validation failed",
            type: TypeFor(ApiErrorCodes.ValidationFailed),
            extensions: new Dictionary<string, object?> { ["code"] = ApiErrorCodes.ValidationFailed });

    public static ValidationProblem Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Turns exceptions into Problem Details: malformed requests → 400, leftover domain rule violations → 422,
/// anything else → 500 without internals (the exception is logged, not returned).
/// </summary>
internal sealed partial class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        int status;
        string code, title, detail;
        switch (exception)
        {
            case BadHttpRequestException bad:
                status = bad.StatusCode;
                code = ApiErrorCodes.MalformedRequest;
                title = "Malformed request";
                detail = bad.InnerException is JsonException json
                    ? $"The request body is not valid JSON for this operation{(json.Path is { } p ? $" (at {p})" : "")}."
                    : bad.Message;
                break;
            case DomainException domain:
                status = StatusCodes.Status422UnprocessableEntity;
                code = domain.Code;
                title = "Business rule violated";
                detail = domain.Message;
                break;
            default:
                LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
                status = StatusCodes.Status500InternalServerError;
                code = ApiErrorCodes.InternalError;
                title = "Internal server error";
                detail = "An unexpected error occurred. It has been logged with the trace ID below.";
                break;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = ApiProblems.TypeFor(code),
                Extensions = { ["code"] = code },
            },
        }).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);
}
