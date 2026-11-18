using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Handles verified AcceptCrypto.NET webhook events; register in DI and map with <c>MapAcceptCryptoWebhook&lt;THandler&gt;</c>.</summary>
public interface IAcceptCryptoWebhookHandler
{
    /// <summary>
    /// Processes one verified event. Throwing makes the endpoint answer 500, so the service retries later.
    /// Duplicates (same <see cref="AcceptCryptoEvent.Id"/>) are filtered by <see cref="IWebhookEventDeduplicator"/> before.
    /// </summary>
    Task HandleAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken);
}

/// <summary>Maps the AcceptCrypto.NET webhook receiver (FR-26).</summary>
public static partial class AcceptCryptoWebhookEndpointExtensions
{
    /// <summary>
    /// Maps <c>POST {pattern}</c>: verifies the signature and timestamp, parses the typed event, skips duplicates and
    /// calls <paramref name="handler"/>. Answers 200 on success or duplicate, 400 for invalid webhooks (forged, altered,
    /// replayed, malformed), 409 while the same event is being processed, 500 when the handler throws.
    /// </summary>
    /// <example><code>app.MapAcceptCryptoWebhook("/webhooks/acceptcrypto", async (AcceptCryptoEvent e) => { … });</code></example>
    public static IEndpointConventionBuilder MapAcceptCryptoWebhook(
        this IEndpointRouteBuilder endpoints, string pattern, Func<AcceptCryptoEvent, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return endpoints.MapAcceptCryptoWebhook(pattern, (e, _) => handler(e));
    }

    /// <summary>
    /// As <see cref="MapAcceptCryptoWebhook(IEndpointRouteBuilder, string, Func{AcceptCryptoEvent, Task})"/>, with the
    /// <see cref="HttpContext"/> (services via <c>RequestServices</c>, cancellation via <c>RequestAborted</c>).
    /// </summary>
    public static IEndpointConventionBuilder MapAcceptCryptoWebhook(
        this IEndpointRouteBuilder endpoints, string pattern, Func<AcceptCryptoEvent, HttpContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfigured(endpoints.ServiceProvider);
        return endpoints.MapPost(pattern, context => HandleRequestAsync(context, handler)).WithDisplayName("AcceptCrypto webhook");
    }

    /// <summary>Maps the webhook receiver with a handler class resolved from DI per request (or created if not registered).</summary>
    public static IEndpointConventionBuilder MapAcceptCryptoWebhook<THandler>(this IEndpointRouteBuilder endpoints, string pattern)
        where THandler : class, IAcceptCryptoWebhookHandler =>
        endpoints.MapAcceptCryptoWebhook(pattern, (e, context) =>
            ActivatorUtilities.GetServiceOrCreateInstance<THandler>(context.RequestServices).HandleAsync(e, context.RequestAborted));

    private static void EnsureConfigured(IServiceProvider services)
    {
        if (services.GetService<IWebhookEventDeduplicator>() is null)
        {
            throw new InvalidOperationException("Call services.AddAcceptCrypto(…) before mapping the AcceptCrypto webhook endpoint.");
        }

        if (services.GetRequiredService<IOptionsMonitor<AcceptCryptoOptions>>().CurrentValue.GetWebhookSecrets().Count == 0)
        {
            throw new InvalidOperationException("No webhook secret configured: set AcceptCrypto:WebhookSecret.");
        }
    }

    private static async Task HandleRequestAsync(HttpContext context, Func<AcceptCryptoEvent, HttpContext, Task> handler)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<IOptionsMonitor<AcceptCryptoOptions>>().CurrentValue;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AcceptCrypto.Sdk.Webhooks");
        var aborted = context.RequestAborted;

        var body = await ReadBodyAsync(context.Request, options.WebhookMaxBodyBytes, aborted).ConfigureAwait(false);
        AcceptCryptoEvent webhookEvent;
        try
        {
            if (body is null)
            {
                throw new AcceptCryptoWebhookException(WebhookRejectionReason.PayloadTooLarge, AcceptCryptoWebhookVerifier.Describe(WebhookRejectionReason.PayloadTooLarge));
            }

            var verifier = new AcceptCryptoWebhookVerifier(
                options.GetWebhookSecrets(), options.WebhookTolerance, services.GetService<TimeProvider>());
            webhookEvent = verifier.VerifyAndParse(
                context.Request.Headers[AcceptCryptoWebhookVerifier.TimestampHeader],
                context.Request.Headers[AcceptCryptoWebhookVerifier.SignatureHeader],
                body);
        }
        catch (AcceptCryptoWebhookException ex)
        {
            LogRejected(logger, ex.Reason, context.Request.Headers[AcceptCryptoWebhookVerifier.EventIdHeader].ToString());
            await NotifyObserversAsync(context, logger, o => o.OnRejectedAsync(context, ex.Reason, body ?? [])).ConfigureAwait(false);
            var status = ex.Reason == WebhookRejectionReason.PayloadTooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest;
            await WriteProblemAsync(context, status, "Webhook rejected", AcceptCryptoWebhookVerifier.Describe(ex.Reason)).ConfigureAwait(false);
            return;
        }

        var deduplicator = services.GetRequiredService<IWebhookEventDeduplicator>();
        var claim = await deduplicator.TryBeginAsync(webhookEvent, aborted).ConfigureAwait(false);
        if (claim != WebhookEventClaim.New)
        {
            await NotifyObserversAsync(context, logger, o => o.OnDuplicateAsync(context, webhookEvent, claim)).ConfigureAwait(false);
        }

        switch (claim)
        {
            case WebhookEventClaim.AlreadyProcessed:
                LogDuplicate(logger, webhookEvent.Id, webhookEvent.Type);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            case WebhookEventClaim.InProgress:
                LogDuplicate(logger, webhookEvent.Id, webhookEvent.Type);
                await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Event in progress", "This event is being processed; retry later.")
                    .ConfigureAwait(false);
                return;
        }

        try
        {
            await handler(webhookEvent, context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            await deduplicator.AbandonAsync(webhookEvent, CancellationToken.None).ConfigureAwait(false);
            return;
        }
#pragma warning disable CA1031 // Any handler failure must turn into a 500 so the service retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await deduplicator.AbandonAsync(webhookEvent, CancellationToken.None).ConfigureAwait(false);
            LogHandlerFailed(logger, ex, webhookEvent.Id, webhookEvent.Type);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "Webhook handler failed", "The event will be retried.")
                .ConfigureAwait(false);
            return;
        }

        await deduplicator.CompleteAsync(webhookEvent, CancellationToken.None).ConfigureAwait(false);
        LogHandled(logger, webhookEvent.Id, webhookEvent.Type);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>Reads the raw body (signatures are over the exact bytes); null when it exceeds the limit.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task NotifyObserversAsync(HttpContext context, ILogger logger, Func<IAcceptCryptoWebhookObserver, Task> notify)
    {
        foreach (var observer in context.RequestServices.GetServices<IAcceptCryptoWebhookObserver>())
        {
            try
            {
                await notify(observer).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Monitoring must never change the webhook response.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogObserverFailed(logger, ex, observer.GetType().Name);
            }
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: status).ExecuteAsync(context);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "AcceptCrypto webhook rejected: {Reason} (event id header {EventId})")]
    private static partial void LogRejected(ILogger logger, WebhookRejectionReason reason, string eventId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AcceptCrypto webhook {EventId} ({EventType}) is a duplicate; skipped")]
    private static partial void LogDuplicate(ILogger logger, Guid eventId, string eventType);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "AcceptCrypto webhook handler failed for {EventId} ({EventType}); answering 500 so it is retried")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, Guid eventId, string eventType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "AcceptCrypto webhook {EventId} ({EventType}) handled")]
    private static partial void LogHandled(ILogger logger, Guid eventId, string eventType);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "AcceptCrypto webhook observer {Observer} failed")]
    private static partial void LogObserverFailed(ILogger logger, Exception exception, string observer);
}
