using System.Text.Json;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.Service.Api;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.PaymentPage;

/// <summary>
/// The payer's public payment page (FR-29, FR-30): <c>/pay/{token}</c> (HTML), <c>/pay/{token}/qr.svg</c> and the
/// status JSON <c>/api/public/invoices/{token}</c> (also used by the scripted payer). No authentication: the
/// unguessable token is the capability, and every response is built from <see cref="PublicInvoiceView"/> only.
/// </summary>
internal static class PaymentPageEndpoints
{
    public const string RateLimitPolicy = "public-payment-page";
    public const string SampleToken = "sample";

    public static IEndpointRouteBuilder MapPaymentPage(this IEndpointRouteBuilder app)
    {
        var page = app.MapGroup("/pay").ExcludeFromDescription().RequireRateLimiting(RateLimitPolicy);
        page.MapGet("/assets/{name}", PaymentPageAssets.Serve);
        page.MapGet("/{token}", PageAsync);
        page.MapGet("/{token}/qr.svg", QrAsync);

        app.MapGet("/api/public/invoices/{token}", StatusAsync)
            .RequireRateLimiting(RateLimitPolicy)
            .WithTags("Public")
            .WithName("GetPublicInvoice")
            .WithSummary("Payment details and live status for the payer (no authentication)")
            .WithDescription("Addressed by the unguessable token from payment_page_url. Returns only what the payer needs "
                + "(network, token, address, amounts, deadline, status, confirmations); never the order ID or metadata.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<IResult> PageAsync(
        string token, string? status, HttpContext http, PaymentPageResolver resolver, PaymentPageRenderer renderer, CancellationToken ct)
    {
        SecurityHeaders(http.Response, html: true);
        var view = await resolver.ResolveAsync(token, status, ct);
        return view is null
            ? Results.Content(PaymentPageRenderer.RenderNotFound(), "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound)
            : Results.Content(renderer.Render(token, status, view), "text/html; charset=utf-8");
    }

    private static async Task<IResult> QrAsync(
        string token, string? mode, string? status, HttpContext http, PaymentPageResolver resolver, CancellationToken ct)
    {
        SecurityHeaders(http.Response, html: false);
        var view = await resolver.ResolveAsync(token, status, ct);
        if (view is null)
        {
            return Results.NotFound();
        }

        var content = mode == "address" ? view.Address : view.PaymentUri ?? view.Address;
        return Results.Content(QrCodes.Svg(content), "image/svg+xml; charset=utf-8");
    }

    private static async Task<Results<Ok<PublicInvoiceView>, ProblemHttpResult>> StatusAsync(
        string token, string? status, HttpContext http, PaymentPageResolver resolver, CancellationToken ct)
    {
        SecurityHeaders(http.Response, html: false);
        var view = await resolver.ResolveAsync(token, status, ct);
        return view is null
            ? ApiProblems.Problem(StatusCodes.Status404NotFound, ApiErrorCodes.InvoiceNotFound, "Invoice not found",
                "No invoice has this payment page token.")
            : TypedResults.Ok(view);
    }

    /// <summary>The token is in the URL: never cache, never send it on as a referrer, keep the page out of search engines.</summary>
    private static void SecurityHeaders(HttpResponse response, bool html)
    {
        var h = response.Headers;
        h.CacheControl = "no-store";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Robots-Tag"] = "noindex, nofollow";
        h.XContentTypeOptions = "nosniff";
        if (html)
        {
            h.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; "
                + "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
            h.XFrameOptions = "DENY";
        }
    }
}

/// <summary>Finds the invoice for a public token (or the Development sample) and maps it to the public view.</summary>
internal sealed class PaymentPageResolver(
    IInvoiceRepository invoices, PublicInvoiceMapper mapper, IOptionsMonitor<PaymentPageOptions> options,
    IOptionsMonitor<ChainOptions> chain, TimeProvider clock)
{
    public async Task<PublicInvoiceView?> ResolveAsync(string token, string? sampleStatus, CancellationToken ct)
    {
        if (token == PaymentPageEndpoints.SampleToken && options.CurrentValue.EnableSample)
        {
            return Sample(sampleStatus);
        }

        // Cheap shape check first: anything that is not a 43-char base64url token cannot exist (no DB round trip).
        if (!IsWellFormed(token))
        {
            return null;
        }

        var invoice = await invoices.GetByPublicTokenAsync(token, ct);
        return invoice is null ? null : mapper.ToView(invoice);
    }

    public static bool IsWellFormed(string token) =>
        token.Length == 43 && token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>Fabricated data for <c>/pay/sample?status=…</c> (design review; enabled only by configuration).</summary>
    private PublicInvoiceView Sample(string? statusName)
    {
        var status = Enum.GetValues<InvoiceStatus>()
            .FirstOrDefault(s => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString()) == statusName, InvoiceStatus.Pending);
        var c = chain.CurrentValue;
        var now = clock.GetUtcNow();
        var amount = UsdcAmount.Parse("25.5");
        var (paid, pending) = status switch
        {
            InvoiceStatus.Confirming => (UsdcAmount.Zero, amount),
            InvoiceStatus.Underpaid => (UsdcAmount.Parse("10"), UsdcAmount.Zero),
            InvoiceStatus.Paid or InvoiceStatus.PaidLate => (amount, UsdcAmount.Zero),
            InvoiceStatus.Overpaid => (UsdcAmount.Parse("30"), UsdcAmount.Zero),
            InvoiceStatus.Expired => (UsdcAmount.Parse("10"), UsdcAmount.Zero),
            _ => (UsdcAmount.Zero, UsdcAmount.Zero),
        };
        var expiresAt = status is InvoiceStatus.Expired or InvoiceStatus.PaidLate ? now.AddMinutes(-5) : now.AddMinutes(24).AddSeconds(30);
        var remaining = amount.SubtractOrZero(paid);
        var accepting = status is InvoiceStatus.Pending or InvoiceStatus.Underpaid;
        const string address = "0x9858EfFD232B4033E47d90003D41EC34EcaEda94";
        return new PublicInvoiceView(
            status, c.NetworkName, c.ChainId, c.TokenSymbol, c.TokenAddress, UsdcAmount.Decimals, address, amount.ToString(),
            paid.ToString(), pending.ToString(), remaining.ToString(), paid.SubtractOrZero(amount).ToString(), expiresAt,
            status == InvoiceStatus.Confirming ? new ConfirmationProgress(1, Math.Max(2, c.Confirmations)) : null,
            accepting ? Eip681.TokenTransfer(c.TokenAddress, c.ChainId, address, remaining) : null, accepting, now);
    }
}

/// <summary>Serialises the view with the API's JSON settings (snake_case) for embedding in the page.</summary>
internal sealed class PublicViewJson(IOptions<JsonOptions> json)
{
    public string Serialize(PublicInvoiceView view) =>
        JsonSerializer.Serialize(view, json.Value.SerializerOptions);
}
