using Microsoft.AspNetCore.Http;

namespace AcceptCrypto.Sdk.Webhooks;

/// <summary>
/// Optional hook into <c>MapAcceptCryptoWebhook</c> for monitoring: called for webhooks that were rejected (forged,
/// altered, replayed, malformed) or skipped as duplicates. Register any number of implementations in DI, e.g.
/// <c>services.AddSingleton&lt;IAcceptCryptoWebhookObserver, MyAuditLog&gt;()</c>. Exceptions thrown by an observer are
/// logged and ignored; they never change the response.
/// </summary>
public interface IAcceptCryptoWebhookObserver
{
    /// <summary>The webhook failed verification or parsing; the endpoint answers 400 (413 when too large).</summary>
    /// <param name="context">The request (headers are available; the body has been read).</param>
    /// <param name="reason">Why it was rejected.</param>
    /// <param name="body">The raw body as received (empty when it exceeded the size limit).</param>
    Task OnRejectedAsync(HttpContext context, WebhookRejectionReason reason, ReadOnlyMemory<byte> body) => Task.CompletedTask;

    /// <summary>A verified event was already processed (or is being processed); the handler is not called again.</summary>
    Task OnDuplicateAsync(HttpContext context, AcceptCryptoEvent webhookEvent, WebhookEventClaim claim) => Task.CompletedTask;
}
