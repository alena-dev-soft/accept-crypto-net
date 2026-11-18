using System.Text.Json;
using System.Text.Json.Serialization;
using AcceptCrypto.Infrastructure.Chain;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.Infrastructure.Webhooks;
using AcceptCrypto.Service;
using AcceptCrypto.Service.Api;
using AcceptCrypto.Service.Configuration;
using AcceptCrypto.Service.Health;
using AcceptCrypto.Service.PaymentPage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

// Configuration (FR-0): appsettings → environment variables → user-secrets (Development); validated on start.
builder.Services.AddAcceptCryptoPersistence(configuration);
builder.Services.AddAcceptCryptoDepositAddresses(configuration);
builder.Services.AddAcceptCryptoChainScanner(configuration);
builder.Services.AddAcceptCryptoWebhooks(configuration); // merchant config (FR-0), SSRF guard, outbox dispatcher
builder.Services.AddOptions<InvoiceOptions>().Bind(configuration.GetSection(InvoiceOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<InvoiceOptions>, InvoiceOptionsValidator>();
builder.Services.AddOptions<ApiOptions>().Bind(configuration.GetSection(ApiOptions.SectionName))
    .Validate(o => o.IsValid(), "Invalid Api configuration (PublicBaseUrl must be an absolute http(s) URL; 1 <= DefaultPageSize <= MaxPageSize).")
    .ValidateOnStart();

// HTTP API: snake_case JSON, enums as snake_case strings, Problem Details for every error (FR-20).
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
});
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
    if (!ctx.ProblemDetails.Extensions.ContainsKey("code"))
    {
        var code = ctx.ProblemDetails.Status switch
        {
            StatusCodes.Status404NotFound => ApiErrorCodes.NotFound,
            StatusCodes.Status400BadRequest => ApiErrorCodes.MalformedRequest,
            >= 500 => ApiErrorCodes.InternalError,
            _ => "http_" + ctx.ProblemDetails.Status,
        };
        ctx.ProblemDetails.Extensions["code"] = code;
        ctx.ProblemDetails.Type ??= ApiProblems.TypeFor(code);
    }
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton<ApiMapper>();

// Public payment page (FR-4, FR-29, FR-30): /pay/{token}, /api/public/invoices/{token}; per-IP rate limit.
builder.Services.AddOptions<PaymentPageOptions>().Bind(configuration.GetSection(PaymentPageOptions.SectionName))
    .Validate(o => o.IsValid(), "Invalid PaymentPage configuration (PollInterval 0.5 s–1 min, RateLimitPerMinute > 0, MerchantDisplayName <= 80 chars).")
    .ValidateOnStart();
builder.Services.AddSingleton<PublicInvoiceMapper>();
builder.Services.AddSingleton<PublicViewJson>();
builder.Services.AddSingleton<PaymentPageResolver>();
builder.Services.AddSingleton<PaymentPageRenderer>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(PaymentPageEndpoints.RateLimitPolicy, http =>
    {
        var limit = http.RequestServices.GetRequiredService<IOptionsMonitor<PaymentPageOptions>>().CurrentValue.RateLimitPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) });
    });
});

// Authentication (FR-17).
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiKeyAuthenticationHandler.MerchantPolicy, p => p
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());

// OpenAPI (FR-21): /openapi/v1.json, UI at /scalar.
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer<ApiKeyOpenApiTransformer>();
    o.AddOperationTransformer<ApiKeyOpenApiTransformer>();
});

// Health (NFR-7): /health = liveness (+ scanner state), /health/ready = also DB and RPC.
var health = builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready]);
if (configuration.GetValue<bool>($"{ChainOptions.SectionName}:{nameof(ChainOptions.Enabled)}"))
{
    health.AddCheck<RpcHealthCheck>("rpc", tags: [HealthTags.Ready]);
}

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<PersistenceOptions>>().Value.MigrateOnStartup)
{
    await app.Services.MigrateAcceptCryptoDatabaseAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = c => !c.Tags.Contains(HealthTags.Ready) });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = HealthReportWriter.WriteAsync });
app.MapOpenApi();
app.MapScalarApiReference();
app.MapMerchantApi();
app.MapPaymentPage();
app.MapGet("/", () => Results.Text(
    "AcceptCrypto.NET service is running. API reference: /scalar, OpenAPI: /openapi/v1.json, health: /health, /health/ready."))
    .ExcludeFromDescription();

StartupLog.Write(app);

await app.RunAsync();
