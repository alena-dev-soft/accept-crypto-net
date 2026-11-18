using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Merchant;
using AcceptCrypto.Infrastructure.Webhooks;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service;

/// <summary>Health check tags.</summary>
internal static class HealthTags
{
    /// <summary>Dependencies (DB, RPC): only on <c>/health/ready</c>.</summary>
    public const string Ready = "ready";
}

/// <summary>Logs a configuration summary at startup — never secret values (NFR-5).</summary>
internal static partial class StartupLog
{
    public static void Write(WebApplication app)
    {
        var merchant = app.Services.GetRequiredService<IOptions<MerchantOptions>>().Value;
        var webhooks = app.Services.GetRequiredService<IOptions<WebhookOptions>>().Value;
        var chain = app.Services.GetRequiredService<IOptions<ChainOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AcceptCrypto.Service");
        LogConfiguration(
            logger,
            merchant.WebhookUrl is { IsAbsoluteUri: true } u ? $"{u.Scheme}://{u.Authority.Split('@')[^1]}" : "(none)",
            merchant.WebhookSecrets.Count,
            webhooks.AllowedHosts.Count,
            chain.ChainId,
            chain.NetworkName,
            chain.Confirmations,
            chain.Enabled);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Merchant configured: webhook origin {WebhookOrigin}, {WebhookSecretCount} webhook secret(s), {AllowlistedHosts} allowlisted webhook host(s); chain {ChainId} ({Network}), {Confirmations} confirmations, scanner enabled: {ScannerEnabled}")]
    private static partial void LogConfiguration(
        ILogger logger, string webhookOrigin, int webhookSecretCount, int allowlistedHosts, long chainId, string network, int confirmations, bool scannerEnabled);
}
