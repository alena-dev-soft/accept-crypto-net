using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcceptCrypto.Domain;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.Service.Configuration;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.Api;

/// <summary>The merchant REST API (<c>/v1</c>, API key required): invoices, unmatched payments, event resend.</summary>
internal static partial class MerchantApi
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private static readonly Dictionary<string, InvoiceStatus> StatusNames = Enum.GetValues<InvoiceStatus>()
        .ToDictionary(s => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString()), StringComparer.Ordinal);

    public static RouteGroupBuilder MapMerchantApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1")
            .RequireAuthorization(ApiKeyAuthenticationHandler.MerchantPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        var invoices = v1.MapGroup("/invoices").WithTags("Invoices");
        invoices.MapPost("/", CreateInvoiceAsync)
            .WithName("CreateInvoice")
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithSummary("Create an invoice")
            .WithDescription("Creates an invoice with its own deposit address and returns the payment details and the payment page link. "
                + "Send an Idempotency-Key header: a retry with the same key and body returns the original invoice (200, Idempotent-Replayed: true).");
        invoices.MapGet("/{id:guid}", GetInvoiceAsync)
            .WithName("GetInvoice")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get an invoice with its transfers and webhook history");
        invoices.MapGet("/", ListInvoicesAsync)
            .WithName("ListInvoices")
            .WithSummary("List invoices, newest first")
            .WithDescription("Filters: status, created_from (inclusive), created_to (exclusive), ISO 8601. Cursor pagination: pass next_cursor as cursor.");
        invoices.MapPost("/{id:guid}/cancel", CancelInvoiceAsync)
            .WithName("CancelInvoice")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Cancel a pending invoice")
            .WithDescription("Only a pending invoice (no payment seen) can be cancelled; otherwise 409. Cancelling a cancelled invoice is a no-op.");

        v1.MapGet("/unmatched-payments", ListUnmatchedAsync)
            .WithTags("Unmatched payments")
            .WithName("ListUnmatchedPayments")
            .WithSummary("List transfers that matched no open invoice, newest first");

        v1.MapPost("/events/{eventId:guid}/resend", ResendEventAsync)
            .WithTags("Events")
            .WithName("ResendEvent")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Queue a webhook event for delivery again")
            .WithDescription("The event keeps its ID and payload, so the merchant can deduplicate it. The retry budget starts over.");

        return v1;
    }

    private static async Task<Results<Created<InvoiceResponse>, Ok<InvoiceResponse>, ValidationProblem, ProblemHttpResult>> CreateInvoiceAsync(
        CreateInvoiceRequest body,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        HttpContext http,
        IInvoiceRepository repository,
        IDepositAddressDeriver deriver,
        IOptionsMonitor<InvoiceOptions> invoiceOptions,
        ApiMapper mapper,
        TimeProvider clock,
        ILogger<MerchantApiLogCategory> logger,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (idempotencyKey is not null && (idempotencyKey.Length is 0 or > 255 || idempotencyKey.Any(c => c is < '!' or > '~')))
        {
            errors[IdempotencyKeyHeader] = ["Must be 1-255 printable ASCII characters without spaces (e.g. a UUID)."];
        }

        UsdcAmount amount = default;
        if (string.IsNullOrWhiteSpace(body.Amount))
        {
            errors["amount"] = ["Required."];
        }
        else if (!UsdcAmount.TryParse(body.Amount, out amount))
        {
            errors["amount"] = ["Must be a decimal string with at most 6 decimals, e.g. \"25.50\"."];
        }

        if (string.IsNullOrWhiteSpace(body.ExternalOrderId))
        {
            errors["external_order_id"] = ["Required."];
        }

        if (body.ExpiresInSeconds is <= 0 or > int.MaxValue)
        {
            errors["expires_in_seconds"] = ["Must be a positive number of seconds."];
        }

        if (body.Metadata is { } md && md.Any(kv => kv.Value is null))
        {
            errors["metadata"] = ["Values must be strings."];
        }

        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var lifetime = body.ExpiresInSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        var policy = invoiceOptions.CurrentValue.ToPolicy();
        var fingerprint = Fingerprint(amount, body.ExternalOrderId!, body.Metadata, body.ExpiresInSeconds);

        InvoiceCreateResult result;
        try
        {
            result = await repository.CreateAsync(
                idempotencyKey,
                fingerprint,
                index => Invoice.Create(
                    new NewInvoice(amount, body.ExternalOrderId!, deriver.DeriveAddress(index), index, body.Metadata, idempotencyKey, lifetime),
                    policy,
                    clock),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (FieldFor(ex.Code) is { } field)
        {
            return ApiProblems.Validation(field, ex.Message);
        }

        var response = mapper.ToResponse(result.Invoice, http.Request);
        switch (result.Outcome)
        {
            case InvoiceCreateOutcome.Created:
                LogInvoiceCreated(logger, result.Invoice.Id, result.Invoice.Amount, result.Invoice.DerivationIndex);
                return TypedResults.Created($"/v1/invoices/{result.Invoice.Id}", response);
            case InvoiceCreateOutcome.Replayed:
                LogInvoiceReplayed(logger, result.Invoice.Id);
                http.Response.Headers[ReplayedHeader] = "true";
                return TypedResults.Ok(response);
            default:
                return ApiProblems.Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    ApiErrorCodes.IdempotencyKeyReused,
                    "Idempotency key reused",
                    "This Idempotency-Key was already used with a different request body. Use a new key for a new invoice.",
                    new Dictionary<string, object?> { ["invoice_id"] = result.Invoice.Id });
        }
    }

    private static async Task<Results<Ok<InvoiceDetailsResponse>, ProblemHttpResult>> GetInvoiceAsync(
        Guid id,
        HttpContext http,
        IInvoiceRepository repository,
        IOutboxRepository outbox,
        ApiMapper mapper,
        CancellationToken cancellationToken)
    {
        var invoice = await repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (invoice is null)
        {
            return InvoiceNotFound(id);
        }

        var events = await outbox.ListByInvoiceAsync(id, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(mapper.ToDetails(invoice, events, http.Request));
    }

    private static async Task<Results<Ok<ListResponse<InvoiceResponse>>, ValidationProblem>> ListInvoicesAsync(
        [FromQuery, Description("Status filter: pending, confirming, underpaid, paid, overpaid, expired, paid_late, cancelled.")] string? status,
        [FromQuery(Name = "created_from"), Description("Created at or after (ISO 8601).")] string? createdFrom,
        [FromQuery(Name = "created_to"), Description("Created before (ISO 8601).")] string? createdTo,
        [FromQuery, Description("next_cursor of the previous page.")] string? cursor,
        [FromQuery, Description("Page size, 1 to Api:MaxPageSize (default Api:DefaultPageSize).")] string? limit,
        HttpContext http,
        IInvoiceRepository repository,
        IOptionsMonitor<ApiOptions> apiOptions,
        ApiMapper mapper,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        InvoiceStatus? statusFilter = null;
        if (status is not null)
        {
            if (StatusNames.TryGetValue(status, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors["status"] = [$"Unknown status. Use one of: {string.Join(", ", StatusNames.Keys)}."];
            }
        }

        var from = ParseDate(createdFrom, "created_from", errors);
        var to = ParseDate(createdTo, "created_to", errors);
        if (from is not null && to is not null && from >= to)
        {
            errors["created_to"] = ["Must be after created_from."];
        }

        InvoiceCursor? after = null;
        if (cursor is not null && !Cursors.TryDecode(cursor, out after))
        {
            errors["cursor"] = ["Invalid cursor. Use next_cursor from a previous page."];
        }

        var pageSize = ParseLimit(limit, apiOptions.CurrentValue, errors);
        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        var page = await repository.ListAsync(new InvoiceListQuery(statusFilter, from, to, after, pageSize), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new ListResponse<InvoiceResponse>(
            page.Items.Select(i => mapper.ToResponse(i, http.Request)).ToList(),
            page.Next is { } next ? Cursors.Encode(next) : null));
    }

    private static async Task<Results<Ok<InvoiceResponse>, ProblemHttpResult>> CancelInvoiceAsync(
        Guid id,
        HttpContext http,
        IInvoiceRepository repository,
        ApiMapper mapper,
        TimeProvider clock,
        ILogger<MerchantApiLogCategory> logger,
        CancellationToken cancellationToken)
    {
        InvoiceUpdate<bool>? update;
        try
        {
            update = await repository.UpdateAsync(
                id,
                invoice => invoice.Status == InvoiceStatus.Cancelled
                    ? new InvoiceMutation<bool>(false, [])
                    : new InvoiceMutation<bool>(true, invoice.Cancel(clock.GetUtcNow())),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Code == DomainErrorCodes.InvalidTransition)
        {
            var current = await repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
            var currentStatus = current is null ? null : JsonNamingPolicy.SnakeCaseLower.ConvertName(current.Status.ToString());
            return ApiProblems.Problem(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.InvoiceNotCancellable,
                "Invoice cannot be cancelled",
                $"Only a pending invoice can be cancelled; this invoice is {currentStatus}.",
                new Dictionary<string, object?> { ["current_status"] = currentStatus });
        }

        if (update is null)
        {
            return InvoiceNotFound(id);
        }

        if (update.Result)
        {
            LogInvoiceCancelled(logger, id);
        }

        return TypedResults.Ok(mapper.ToResponse(update.Invoice, http.Request));
    }

    private static async Task<Results<Ok<ListResponse<UnmatchedPaymentResponse>>, ValidationProblem>> ListUnmatchedAsync(
        [FromQuery, Description("next_cursor of the previous page.")] string? cursor,
        [FromQuery, Description("Page size, 1 to Api:MaxPageSize (default Api:DefaultPageSize).")] string? limit,
        IUnmatchedPaymentRepository repository,
        IOptionsMonitor<ApiOptions> apiOptions,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        long? beforeId = null;
        if (cursor is not null)
        {
            if (Cursors.TryDecode(cursor, out long id))
            {
                beforeId = id;
            }
            else
            {
                errors["cursor"] = ["Invalid cursor. Use next_cursor from a previous page."];
            }
        }

        var pageSize = ParseLimit(limit, apiOptions.CurrentValue, errors);
        if (errors.Count > 0)
        {
            return ApiProblems.Validation(errors);
        }

        // One extra row tells whether there is a next page.
        var rows = await repository.ListAsync(beforeId, pageSize + 1, cancellationToken).ConfigureAwait(false);
        var items = rows.Take(pageSize).Select(ApiMapper.ToResponse).ToList();
        var next = rows.Count > pageSize ? Cursors.Encode(items[^1].Id) : null;
        return TypedResults.Ok(new ListResponse<UnmatchedPaymentResponse>(items, next));
    }

    private static async Task<Results<Accepted<EventResponse>, ProblemHttpResult>> ResendEventAsync(
        Guid eventId,
        IOutboxRepository outbox,
        TimeProvider clock,
        ILogger<MerchantApiLogCategory> logger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var updated = await outbox.UpdateAsync(eventId, e => e.Resend(clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                if (updated is null)
                {
                    return ApiProblems.Problem(
                        StatusCodes.Status404NotFound, ApiErrorCodes.EventNotFound, "Event not found", $"No webhook event with ID {eventId}.");
                }

                LogEventResent(logger, eventId, updated.Type);
                return TypedResults.Accepted($"/v1/invoices/{updated.InvoiceId}", ApiMapper.ToResponse(updated));
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // The dispatcher touched the row meanwhile: reload and resend again.
            }
            catch (DbUpdateConcurrencyException)
            {
                return ApiProblems.Problem(
                    StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, "Conflict", "The event is being delivered right now; try again.");
            }
        }
    }

    private static ProblemHttpResult InvoiceNotFound(Guid id) =>
        ApiProblems.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.InvoiceNotFound, "Invoice not found", $"No invoice with ID {id}.");

    private static string? FieldFor(string domainCode) => domainCode switch
    {
        DomainErrorCodes.AmountOutOfRange => "amount",
        DomainErrorCodes.LifetimeOutOfRange => "expires_in_seconds",
        DomainErrorCodes.InvalidExternalOrderId => "external_order_id",
        DomainErrorCodes.InvalidMetadata => "metadata",
        DomainErrorCodes.InvalidIdempotencyKey => IdempotencyKeyHeader,
        _ => null,
    };

    /// <summary>SHA-256 over the canonical request (amount in base units, sorted metadata), for FR-2 key reuse detection.</summary>
    internal static string Fingerprint(UsdcAmount amount, string externalOrderId, IReadOnlyDictionary<string, string>? metadata, long? expiresInSeconds)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            amount.ToFixedString(),
            externalOrderId,
            (metadata ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[] { kv.Key, kv.Value }).ToArray(),
            expiresInSeconds,
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static DateTimeOffset? ParseDate(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        errors[field] = ["Must be an ISO 8601 date or timestamp, e.g. 2026-11-20T12:00:00Z."];
        return null;
    }

    private static int ParseLimit(string? value, ApiOptions options, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return options.DefaultPageSize;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit >= 1 && limit <= options.MaxPageSize)
        {
            return limit;
        }

        errors["limit"] = [$"Must be an integer from 1 to {options.MaxPageSize}."];
        return options.DefaultPageSize;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Invoice {InvoiceId} created: amount {Amount} USDC, derivation index {DerivationIndex}")]
    private static partial void LogInvoiceCreated(ILogger logger, Guid invoiceId, UsdcAmount amount, long derivationIndex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invoice {InvoiceId} returned for a replayed idempotency key")]
    private static partial void LogInvoiceReplayed(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Invoice {InvoiceId} cancelled by the merchant")]
    private static partial void LogInvoiceCancelled(ILogger logger, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook event {EventId} ({EventType}) queued for resend")]
    private static partial void LogEventResent(ILogger logger, Guid eventId, string eventType);
}

/// <summary>Log category of the merchant API endpoints.</summary>
internal sealed class MerchantApiLogCategory;
