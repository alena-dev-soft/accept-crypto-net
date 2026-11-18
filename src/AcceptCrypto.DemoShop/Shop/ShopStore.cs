using System.Text.Json;
using System.Text.Json.Serialization;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.DemoShop.Shop;

/// <summary>
/// The demo shop's "database": orders, the webhook log and processed event IDs in one JSON file, written atomically
/// (temp file + rename) after every change, so orders and deduplication survive a restart of the shop (S5).
/// It also is the shop's <see cref="IWebhookEventDeduplicator"/>: applying an event and remembering its ID happen in
/// one write, the same pattern as "update the order and insert the event ID in one transaction" in a real database.
/// </summary>
internal sealed partial class ShopStore : IWebhookEventDeduplicator, IAcceptCryptoWebhookObserver
{
    public const string AttackHeader = "X-Demo-Attack";
    private const int MaxLogEntries = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, DateTimeOffset> _inProgress = [];
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<ShopStore> _logger;
    private readonly ShopState _state;

    public ShopStore(IOptions<ShopOptions> options, IHostEnvironment environment, TimeProvider time, ILogger<ShopStore> logger)
    {
        _path = Path.GetFullPath(options.Value.DataPath, environment.ContentRootPath);
        _time = time;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _state = File.Exists(_path)
            ? JsonSerializer.Deserialize<ShopState>(File.ReadAllText(_path), Json) ?? new ShopState()
            : new ShopState();
        LogLoaded(_logger, _path, _state.Orders.Count);
    }

    public IReadOnlyList<ShopOrder> Orders()
    {
        lock (_lock)
        {
            return [.. _state.Orders.OrderByDescending(o => o.CreatedAt)];
        }
    }

    public ShopOrder? FindOrder(string id)
    {
        lock (_lock)
        {
            return _state.Orders.Find(o => o.Id == id);
        }
    }

    public IReadOnlyList<WebhookLogEntry> WebhookLog(string? orderId = null)
    {
        lock (_lock)
        {
            return [.. _state.WebhookLog.Where(e => orderId is null || e.OrderId == orderId).Reverse()];
        }
    }

    public CapturedWebhook? LastGenuineWebhook()
    {
        lock (_lock)
        {
            return _state.LastGenuineWebhook;
        }
    }

    public void SaveOrder(ShopOrder order)
    {
        lock (_lock)
        {
            var index = _state.Orders.FindIndex(o => o.Id == order.Id);
            if (index >= 0)
            {
                _state.Orders[index] = order;
            }
            else
            {
                _state.Orders.Add(order);
            }

            Persist();
        }
    }

    /// <summary>
    /// Applies a verified event to its order and remembers the event ID, in one write. Events are delivered in order
    /// per invoice; an older event (manual resend) is logged but does not move the order back.
    /// </summary>
    public void ApplyEvent(AcceptCryptoEvent webhookEvent, HttpContext context)
    {
        var headers = context.Request.Headers;
        var attack = NullIfEmpty(headers[AttackHeader].ToString());
        lock (_lock)
        {
            var invoice = webhookEvent.Invoice;
            var index = _state.Orders.FindIndex(o => o.Id == invoice.ExternalOrderId || o.InvoiceId == invoice.Id);
            var status = OrderStatus.FromInvoice(invoice.Status);
            string outcome;
            string detail;
            if (index < 0)
            {
                outcome = WebhookOutcome.UnknownOrder;
                detail = $"Verified, but no order {invoice.ExternalOrderId} in this shop; acknowledged.";
            }
            else if (_state.Orders[index].LastEventAt is { } last && webhookEvent.CreatedAt < last)
            {
                outcome = WebhookOutcome.Stale;
                detail = "Older than the last applied event (e.g. a manual resend); order status unchanged.";
            }
            else
            {
                var order = _state.Orders[index];
                _state.Orders[index] = order with
                {
                    Status = status,
                    InvoiceId = invoice.Id,
                    ConfirmedTotal = invoice.ConfirmedTotal.ToString(),
                    LastEventAt = webhookEvent.CreatedAt,
                };
                outcome = WebhookOutcome.Processed;
                detail = webhookEvent switch
                {
                    PaymentRevertedEvent reverted =>
                        $"Reorg: payment of {reverted.Transfer.Amount} USDC (tx {Short(reverted.Transfer.TxHash)}) disappeared from the chain; order is {status}.",
                    InvoiceStatusChangedEvent changed =>
                        $"Invoice {OrderStatus.FromInvoice(changed.PreviousStatus)} → {status}; confirmed {invoice.ConfirmedTotal} of {invoice.Amount} USDC.",
                    _ => $"Order is {status}; confirmed {invoice.ConfirmedTotal} of {invoice.Amount} USDC.",
                };
            }

            _state.ProcessedEventIds.Add(webhookEvent.Id);
            if (attack is null)
            {
                _state.LastGenuineWebhook = new CapturedWebhook
                {
                    Body = webhookEvent.RawJson,
                    Timestamp = headers[AcceptCryptoWebhookVerifier.TimestampHeader].ToString(),
                    Signature = headers[AcceptCryptoWebhookVerifier.SignatureHeader].ToString(),
                    EventId = webhookEvent.Id.ToString(),
                    EventType = webhookEvent.Type,
                    ReceivedAt = _time.GetUtcNow(),
                };
            }

            AddLog(new WebhookLogEntry
            {
                ReceivedAt = _time.GetUtcNow(),
                Outcome = outcome,
                Detail = detail,
                EventId = webhookEvent.Id.ToString(),
                EventType = webhookEvent.Type,
                OrderId = invoice.ExternalOrderId,
                InvoiceStatus = status,
                Attack = attack,
            });
            Persist();
        }
    }

    // IWebhookEventDeduplicator (FR-16): persistent, so a redelivery after a shop restart is still detected.
    public ValueTask<WebhookEventClaim> TryBeginAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_state.ProcessedEventIds.Contains(webhookEvent.Id))
            {
                return ValueTask.FromResult(WebhookEventClaim.AlreadyProcessed);
            }

            var now = _time.GetUtcNow();
            if (_inProgress.TryGetValue(webhookEvent.Id, out var since) && now - since < TimeSpan.FromMinutes(1))
            {
                return ValueTask.FromResult(WebhookEventClaim.InProgress);
            }

            _inProgress[webhookEvent.Id] = now;
            return ValueTask.FromResult(WebhookEventClaim.New);
        }
    }

    public ValueTask CompleteAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _inProgress.Remove(webhookEvent.Id);
            if (_state.ProcessedEventIds.Add(webhookEvent.Id))
            {
                Persist();
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AbandonAsync(AcceptCryptoEvent webhookEvent, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _inProgress.Remove(webhookEvent.Id);
        }

        return ValueTask.CompletedTask;
    }

    // IAcceptCryptoWebhookObserver: rejected and duplicate webhooks go to the log too (FR-26 demo).
    public Task OnRejectedAsync(HttpContext context, WebhookRejectionReason reason, ReadOnlyMemory<byte> body)
    {
        var headers = context.Request.Headers;
        lock (_lock)
        {
            AddLog(new WebhookLogEntry
            {
                ReceivedAt = _time.GetUtcNow(),
                Outcome = WebhookOutcome.Rejected,
                Detail = $"{reason}: {AcceptCryptoWebhookVerifier.Describe(reason)}",
                EventId = NullIfEmpty(headers[AcceptCryptoWebhookVerifier.EventIdHeader].ToString()),
                EventType = NullIfEmpty(headers[AcceptCryptoWebhookVerifier.EventTypeHeader].ToString()),
                Attack = NullIfEmpty(headers[AttackHeader].ToString()),
            });
            Persist();
        }

        return Task.CompletedTask;
    }

    public Task OnDuplicateAsync(HttpContext context, AcceptCryptoEvent webhookEvent, WebhookEventClaim claim)
    {
        lock (_lock)
        {
            AddLog(new WebhookLogEntry
            {
                ReceivedAt = _time.GetUtcNow(),
                Outcome = WebhookOutcome.Duplicate,
                Detail = claim == WebhookEventClaim.InProgress
                    ? "Same event ID is being processed right now; answered 409, the service retries."
                    : "Event ID already processed; acknowledged (200) without applying it again.",
                EventId = webhookEvent.Id.ToString(),
                EventType = webhookEvent.Type,
                OrderId = webhookEvent.Invoice.ExternalOrderId,
                InvoiceStatus = OrderStatus.FromInvoice(webhookEvent.Invoice.Status),
                Attack = NullIfEmpty(context.Request.Headers[AttackHeader].ToString()),
            });
            Persist();
        }

        return Task.CompletedTask;
    }

    private void AddLog(WebhookLogEntry entry)
    {
        _state.WebhookLog.Add(entry);
        if (_state.WebhookLog.Count > MaxLogEntries)
        {
            _state.WebhookLog.RemoveRange(0, _state.WebhookLog.Count - MaxLogEntries);
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Short(string hash) => hash.Length > 12 ? hash[..10] + "…" : hash;

    [LoggerMessage(Level = LogLevel.Information, Message = "Shop data: {Path} ({Orders} orders)")]
    private static partial void LogLoaded(ILogger logger, string path, int orders);
}
