using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.DemoShop.Shop;

/// <summary>Result of one simulated attack: what was sent and how the shop's webhook endpoint answered.</summary>
internal sealed record AttackResult(string Kind, string Description, int StatusCode, string Response);

/// <summary>
/// FR-26 demo: sends bad webhooks to the shop's own SDK endpoint, as an attacker on the network would.
/// <list type="bullet">
/// <item><c>forged</c> — a made-up <c>invoice.paid</c> signed with a secret the attacker guessed → 400.</item>
/// <item><c>tampered</c> — the last genuine webhook with the status changed to paid, original signature → 400.</item>
/// <item><c>replay</c> — the last genuine webhook resent byte for byte → 400 if older than 5 min, else a detected duplicate.</item>
/// <item><c>stale</c> — a genuine webhook as if captured 10 minutes ago (signed then) and replayed now → 400.</item>
/// </list>
/// Requests carry <c>X-Demo-Attack</c> only so the log can label them; the endpoint ignores it.
/// </summary>
internal sealed class WebhookAttacks(
    IHttpClientFactory httpClientFactory,
    ShopStore store,
    IServer server,
    IOptionsMonitor<AcceptCryptoOptions> sdkOptions,
    IOptions<ShopOptions> shopOptions,
    TimeProvider time)
{
    public const string WebhookPath = "/webhooks/acceptcrypto";
    public static readonly string[] Kinds = ["forged", "tampered", "replay", "stale"];

    public async Task<AttackResult> RunAsync(string kind, CancellationToken cancellationToken)
    {
        var genuine = store.LastGenuineWebhook();
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        string body;
        string timestamp;
        string signature;
        string description;
        switch (kind)
        {
            case "forged":
                body = MarkPaid(genuine?.Body ?? SyntheticBody());
                timestamp = Unix(now);
                signature = AcceptCryptoWebhookVerifier.ComputeSignatureHeader(["whsec_guessed_by_attacker_0000000000000"], now, Encoding.UTF8.GetBytes(body));
                description = "Made-up invoice.paid event signed with a guessed secret.";
                break;
            case "tampered" when genuine is not null:
                body = MarkPaid(genuine.Body);
                timestamp = genuine.Timestamp;
                signature = genuine.Signature;
                description = $"Genuine {genuine.EventType} webhook with the status changed to paid, original signature kept.";
                break;
            case "replay" when genuine is not null:
                body = genuine.Body;
                timestamp = genuine.Timestamp;
                signature = genuine.Signature;
                description = $"Genuine {genuine.EventType} webhook (event {genuine.EventId[..8]}…) resent byte for byte, captured at {genuine.ReceivedAt:HH:mm:ss} UTC.";
                break;
            case "stale" when genuine is not null:
                var then = now - 600;
                body = genuine.Body;
                timestamp = Unix(then);
                signature = AcceptCryptoWebhookVerifier.ComputeSignatureHeader(
                    sdkOptions.CurrentValue.GetWebhookSecrets(), then, Encoding.UTF8.GetBytes(body));
                description = "Genuine webhook as if captured 10 minutes ago (validly signed then) and replayed now.";
                break;
            case "tampered" or "replay" or "stale":
                return new AttackResult(kind, "Needs a genuine webhook first: place an order so the service sends one.", 409, string.Empty);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown attack.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SelfUrl(), WebhookPath))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var eventId = JsonNode.Parse(body)?["id"]?.GetValue<string>() ?? string.Empty;
        request.Headers.Add(AcceptCryptoWebhookVerifier.EventIdHeader, eventId);
        request.Headers.Add(AcceptCryptoWebhookVerifier.EventTypeHeader, JsonNode.Parse(body)?["type"]?.GetValue<string>() ?? "invoice.paid");
        request.Headers.Add(AcceptCryptoWebhookVerifier.TimestampHeader, timestamp);
        request.Headers.Add(AcceptCryptoWebhookVerifier.SignatureHeader, signature);
        request.Headers.Add(ShopStore.AttackHeader, kind);

        using var response = await httpClientFactory.CreateClient("attacks").SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new AttackResult(kind, description, (int)response.StatusCode, text);
    }

    /// <summary>Turns any event body into an <c>invoice.paid</c> with a new event ID (so it is not a mere duplicate).</summary>
    private static string MarkPaid(string body)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        node["id"] = Guid.NewGuid().ToString();
        node["type"] = AcceptCryptoEventTypes.InvoicePaid;
        if (node["data"]?["invoice"] is JsonObject invoice)
        {
            invoice["status"] = "paid";
            invoice["confirmed_total"] = invoice["amount"]?.GetValue<string>() ?? "0";
            invoice["remaining_amount"] = "0";
        }

        return node.ToJsonString();
    }

    private static string SyntheticBody()
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = AcceptCryptoEventTypes.InvoicePaid,
            ["created_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["api_version"] = "2026-11-17",
            ["data"] = new JsonObject
            {
                ["invoice"] = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["external_order_id"] = "ORD-FAKE",
                    ["status"] = "paid",
                    ["amount"] = "1000",
                    ["confirmed_total"] = "1000",
                    ["remaining_amount"] = "0",
                    ["deposit_address"] = "0x0000000000000000000000000000000000000001",
                    ["created_at"] = now.ToString("O", CultureInfo.InvariantCulture),
                    ["expires_at"] = now.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
                    ["metadata"] = new JsonObject(),
                },
                ["previous_status"] = "pending",
            },
        }.ToJsonString();
    }

    private static string Unix(long seconds) => seconds.ToString(CultureInfo.InvariantCulture);

    /// <summary>The shop's own address (configured, or the first listening address with a loopback host).</summary>
    private Uri SelfUrl()
    {
        if (shopOptions.Value.SelfUrl is { } configured)
        {
            return configured;
        }

        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .OrderBy(a => a.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? 1 : 0).FirstOrDefault()
            ?? "http://localhost:8080";
        var builder = new UriBuilder(address.Replace("://+", "://localhost", StringComparison.Ordinal)
            .Replace("://*", "://localhost", StringComparison.Ordinal)
            .Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal));
        return builder.Uri;
    }
}
