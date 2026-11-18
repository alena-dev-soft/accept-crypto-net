using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Webhooks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace AcceptCrypto.Service.Observability;

/// <summary>Metrics endpoint settings (section <c>Observability</c>).</summary>
public sealed class ObservabilityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Observability";

    /// <summary>Expose metrics in the Prometheus text format. Default on.</summary>
    public bool MetricsEnabled { get; set; } = true;

    /// <summary>Path of the scraping endpoint. Default <c>/metrics</c>.</summary>
    public string MetricsPath { get; set; } = "/metrics";
}

/// <summary>
/// NFR-7 metrics: the service's own meters (<c>AcceptCrypto.Chain</c>: lag behind the chain head, head/scanned block,
/// transfers, reorgs, failed passes; <c>AcceptCrypto.Webhooks</c>: queue length, due, failed, oldest pending age,
/// attempts by outcome, attempt duration) plus the built-in ASP.NET Core, HttpClient and runtime meters, exported via
/// OpenTelemetry in the Prometheus format (<c>GET /metrics</c>).
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>Meters exported besides the service's own.</summary>
    internal static readonly string[] BuiltInMeters =
    [
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.RateLimiting",
        "System.Net.Http",
        "System.Runtime",
    ];

    /// <summary>Registers OpenTelemetry metrics with the Prometheus exporter (unless disabled).</summary>
    public static IServiceCollection AddAcceptCryptoObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        services.AddSingleton(options);
        if (!options.MetricsEnabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("acceptcrypto-service"))
            .WithMetrics(metrics => metrics
                .AddMeter(ChainScanState.MeterName, WebhookMetrics.MeterName)
                .AddMeter(BuiltInMeters)
                .AddPrometheusExporter());
        return services;
    }

    /// <summary>Maps the Prometheus scraping endpoint (unauthenticated, like <c>/health</c>; keep it off the public
    /// internet with the reverse proxy, or disable it with <c>Observability:MetricsEnabled=false</c>).</summary>
    public static IEndpointRouteBuilder MapAcceptCryptoMetrics(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<ObservabilityOptions>();
        if (options.MetricsEnabled)
        {
            app.MapPrometheusScrapingEndpoint(options.MetricsPath);
        }

        return app;
    }
}
