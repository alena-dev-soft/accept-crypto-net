using AcceptCrypto.Sdk;

namespace AcceptCrypto.DemoShop.Shop;

/// <summary>A product of the demo catalog (prices are decimal USDC strings, as the API expects).</summary>
internal sealed record Product(string Id, string Name, string Description, string Price, string Emoji);

/// <summary>The fixed demo catalog.</summary>
internal static class Catalog
{
    public static readonly IReadOnlyList<Product> Products =
    [
        new("stickers", "Sticker pack", "Five .NET + Ethereum stickers.", "5.00", "🏷️"),
        new("tshirt", "T-shirt", "Organic cotton, \"I accept USDC\" print.", "25.00", "👕"),
        new("course", "Online course", "Accepting USDC in a .NET app, 3 hours.", "49.99", "🎓"),
    ];

    public static Product? Find(string? id) => Products.FirstOrDefault(p => p.Id == id);
}

/// <summary>Order states of the shop: the invoice status plus two states before the invoice exists.</summary>
internal static class OrderStatus
{
    public const string Creating = "creating_invoice";
    public const string InvoiceFailed = "invoice_failed";

    public static string FromInvoice(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Pending => "pending",
        InvoiceStatus.Confirming => "confirming",
        InvoiceStatus.Underpaid => "underpaid",
        InvoiceStatus.Paid => "paid",
        InvoiceStatus.Overpaid => "overpaid",
        InvoiceStatus.Expired => "expired",
        InvoiceStatus.PaidLate => "paid_late",
        InvoiceStatus.Cancelled => "cancelled",
        _ => "unknown",
    };

    /// <summary>What the shop does with an order in this state (shown next to the status).</summary>
    public static string Describe(string status) => status switch
    {
        Creating => "Creating the invoice…",
        InvoiceFailed => "The payment service could not create an invoice.",
        "pending" => "Waiting for the payment.",
        "confirming" => "Payment seen on-chain, waiting for confirmations. Do not ship yet.",
        "underpaid" => "Partly paid; waiting for the rest.",
        "paid" => "Paid. Ship the order.",
        "overpaid" => "Paid more than the price. Ship and refund the difference manually.",
        "expired" => "Not paid in time.",
        "paid_late" => "Paid after the deadline. Decide whether to honour it.",
        "cancelled" => "Cancelled.",
        _ => "Unknown status.",
    };
}

/// <summary>An order of the demo shop.</summary>
internal sealed record ShopOrder
{
    public required string Id { get; init; }
    public required string ProductId { get; init; }
    public required string ProductName { get; init; }
    public required string Amount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Status { get; init; }
    public Guid? InvoiceId { get; init; }
    public string? PaymentPageUrl { get; init; }
    public string? DepositAddress { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ConfirmedTotal { get; init; }
    public string? Error { get; init; }

    /// <summary>Creation time of the last applied event: an older event (e.g. a manual resend) does not move the status back.</summary>
    public DateTimeOffset? LastEventAt { get; init; }
}

/// <summary>Outcome of one webhook request, as shown in the log.</summary>
internal static class WebhookOutcome
{
    public const string Processed = "processed";
    public const string Stale = "processed_stale";
    public const string UnknownOrder = "unknown_order";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";
}

/// <summary>One line of the webhook log (received, duplicate or rejected).</summary>
internal sealed record WebhookLogEntry
{
    public required DateTimeOffset ReceivedAt { get; init; }
    public required string Outcome { get; init; }
    public required string Detail { get; init; }
    public string? EventId { get; init; }
    public string? EventType { get; init; }
    public string? OrderId { get; init; }
    public string? InvoiceStatus { get; init; }

    /// <summary>Set for requests sent by the demo attack buttons (header <c>X-Demo-Attack</c>).</summary>
    public string? Attack { get; init; }
}

/// <summary>The last genuine webhook exactly as received (body and headers), used by the replay attack demo.</summary>
internal sealed record CapturedWebhook
{
    public required string Body { get; init; }
    public required string Timestamp { get; init; }
    public required string Signature { get; init; }
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>Everything the shop persists.</summary>
internal sealed class ShopState
{
    public List<ShopOrder> Orders { get; init; } = [];
    public List<WebhookLogEntry> WebhookLog { get; init; } = [];
    public HashSet<Guid> ProcessedEventIds { get; init; } = [];
    public CapturedWebhook? LastGenuineWebhook { get; set; }
}

/// <summary>Demo shop settings (section <c>Shop</c>).</summary>
internal sealed class ShopOptions
{
    public const string SectionName = "Shop";

    /// <summary>JSON file with orders, the webhook log and processed event IDs (survives restarts, S5).</summary>
    public string DataPath { get; set; } = "data/shop.json";

    /// <summary>Base URL the attack demo posts to (the shop itself); default: the shop's own listening address.</summary>
    public Uri? SelfUrl { get; set; }

    /// <summary>Command shown on order pages to pay with the scripted payer; empty = hide the hint (testnet, docs/testnet.md).</summary>
    public string PayerCommand { get; set; } = "dotnet run --project src/AcceptCrypto.Payer --";
}
