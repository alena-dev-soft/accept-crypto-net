using AcceptCrypto.Sdk;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace AcceptCrypto.DemoShop.Shop;

/// <summary>Server-rendered HTML of the demo shop (no front-end build; every value is HTML-encoded).</summary>
internal static class Pages
{
    private const string Css = """
        :root{--fg:#1d2433;--muted:#667085;--line:#e4e7ec;--bg:#f7f8fa;--accent:#2563eb}
        *{box-sizing:border-box}body{margin:0;font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:var(--fg);background:var(--bg)}
        header{background:#101828;color:#fff;padding:12px 24px;display:flex;gap:24px;align-items:center}
        header a{color:#d0d5dd;text-decoration:none}header a:hover,header a.on{color:#fff}header b{margin-right:auto}
        main{max-width:1080px;margin:24px auto;padding:0 24px}h1{font-size:22px;margin:0 0 16px}h2{font-size:17px;margin:28px 0 10px}
        .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:16px}
        .card{background:#fff;border:1px solid var(--line);border-radius:10px;padding:16px}
        .emoji{font-size:40px}.price{font-size:20px;font-weight:600;margin:6px 0}
        button,.btn{background:var(--accent);color:#fff;border:0;border-radius:6px;padding:8px 14px;font:inherit;cursor:pointer;text-decoration:none;display:inline-block}
        button.secondary{background:#475467}select{font:inherit;padding:6px;border-radius:6px;border:1px solid var(--line)}
        table{width:100%;border-collapse:collapse;background:#fff;border:1px solid var(--line);border-radius:10px;overflow:hidden}
        th,td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--line);vertical-align:top;font-size:14px}th{background:#f2f4f7;font-weight:600}
        code,.mono{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:13px}
        pre{background:#101828;color:#e4e7ec;padding:12px;border-radius:8px;overflow:auto;font-size:13px}
        .muted{color:var(--muted)}.badge{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:600;background:#eaecf0}
        .s-paid,.s-processed{background:#d1fadf;color:#05603a}.s-confirming,.s-pending,.s-creating_invoice{background:#fef0c7;color:#93370d}
        .s-underpaid,.s-overpaid,.s-paid_late,.s-duplicate,.s-processed_stale,.s-unknown_order{background:#e0eaff;color:#1d3a8a}
        .s-expired,.s-cancelled,.s-invoice_failed,.s-rejected{background:#fee4e2;color:#912018}
        .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}.note{background:#fff;border-left:4px solid var(--accent);padding:10px 14px;margin:16px 0}
        td:first-child,td a{white-space:nowrap}
        dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:0}dt{color:var(--muted)}dd{margin:0}
        """;

    private static readonly HtmlEncoder Html = HtmlEncoder.Default;

    public static IResult Catalog(IReadOnlyList<ShopOrder> recent)
    {
        var b = new StringBuilder();
        b.Append("<h1>Demo shop — pay with USDC</h1><p class=\"muted\">Each order creates an invoice through the AcceptCrypto.NET SDK and sends you to its payment page. The shop learns the result from signed webhooks.</p><div class=\"grid\">");
        foreach (var p in Shop.Catalog.Products)
        {
            b.Append(CultureInfo.InvariantCulture, $"""
                <form class="card" method="post" action="/checkout">
                  <div class="emoji">{E(p.Emoji)}</div><b>{E(p.Name)}</b><div class="muted">{E(p.Description)}</div>
                  <div class="price">{E(p.Price)} USDC</div>
                  <input type="hidden" name="product" value="{E(p.Id)}">
                  <div class="row"><select name="lifetime" title="Invoice lifetime">
                    <option value="">expires in 30 min</option><option value="2">expires in 2 min (S4 demo)</option></select>
                  <button>Buy with USDC</button></div>
                </form>
                """);
        }

        b.Append("</div>");
        if (recent.Count > 0)
        {
            b.Append("<h2>Recent orders</h2>").Append(OrderTable(recent.Take(5)));
        }

        return Page("Shop", "shop", b.ToString());
    }

    public static IResult OrderList(IReadOnlyList<ShopOrder> orders) =>
        Page("Orders", "orders", "<h1>Orders</h1>" + (orders.Count == 0 ? "<p class=\"muted\">No orders yet.</p>" : OrderTable(orders)), refreshSeconds: 3);

    public static IResult Order(ShopOrder order, IReadOnlyList<WebhookLogEntry> log, InvoiceDetails? invoice, string? serviceError, string payerCommand)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"<h1>Order {E(order.Id)} {Badge(order.Status)}</h1><p>{E(OrderStatus.Describe(order.Status))}</p>");
        b.Append("<div class=\"card\"><dl>");
        b.Append(CultureInfo.InvariantCulture, $"<dt>Product</dt><dd>{E(order.ProductName)}</dd><dt>Price</dt><dd>{E(order.Amount)} USDC</dd>");
        b.Append(CultureInfo.InvariantCulture, $"<dt>Confirmed</dt><dd>{E(order.ConfirmedTotal ?? "0")} USDC</dd><dt>Created</dt><dd>{Time(order.CreatedAt)}</dd>");
        if (order.InvoiceId is { } id)
        {
            b.Append(CultureInfo.InvariantCulture, $"<dt>Invoice</dt><dd class=\"mono\">{id}</dd>");
        }

        if (order.DepositAddress is not null)
        {
            b.Append(CultureInfo.InvariantCulture, $"<dt>Deposit address</dt><dd class=\"mono\">{E(order.DepositAddress)}</dd><dt>Expires</dt><dd>{Time(order.ExpiresAt)}</dd>");
        }

        if (order.Error is not null)
        {
            b.Append(CultureInfo.InvariantCulture, $"<dt>Error</dt><dd>{E(order.Error)}</dd>");
        }

        b.Append("</dl>");
        if (order.PaymentPageUrl is not null)
        {
            b.Append(CultureInfo.InvariantCulture, $"<p><a class=\"btn\" href=\"{E(order.PaymentPageUrl)}\">Open the payment page</a></p>");
            b.Append(CultureInfo.InvariantCulture, $"""
                <p class="muted">No wallet? Pay with the scripted payer (local chain):</p>
                <pre>{E(payerCommand)} {E(order.PaymentPageUrl)}</pre>
                <p class="muted">Add <code>--amount 1</code> (underpaid), <code>--amount 100</code> (overpaid) or <code>--split 2</code> (two partial transfers).</p>
                """);
        }

        b.Append("</div><h2>Webhooks received for this order</h2>").Append(LogTable(log, showOrder: false));
        b.Append("<h2>Payment service view <span class=\"muted\">(SDK <code>GetInvoiceAsync</code>: transfers and webhook delivery history)</span></h2>");
        if (invoice is not null)
        {
            b.Append(CultureInfo.InvariantCulture, $"<p>Invoice status: {Badge(OrderStatus.FromInvoice(invoice.Status))} confirmed {E(invoice.ConfirmedTotal.ToString())}, unconfirmed {E(invoice.UnconfirmedTotal.ToString())}, remaining {E(invoice.RemainingAmount.ToString())} USDC</p>");
            b.Append("<table><tr><th>Transfer</th><th>Amount</th><th>Block</th><th>State</th><th>Confirmations</th><th>On time</th></tr>");
            foreach (var t in invoice.Transfers)
            {
                b.Append(CultureInfo.InvariantCulture, $"<tr><td class=\"mono\">{E(Short(t.TxHash))}#{t.LogIndex}</td><td>{E(t.Amount.ToString())}</td><td>{t.BlockNumber}</td><td>{E(t.State.ToString())}</td><td>{t.Confirmations?.ToString(CultureInfo.InvariantCulture) ?? "–"}</td><td>{(t.OnTime ? "yes" : "no")}</td></tr>");
            }

            b.Append(invoice.Transfers.Count == 0 ? "<tr><td colspan=\"6\" class=\"muted\">No transfers yet.</td></tr>" : string.Empty).Append("</table><br>");
            b.Append("<table><tr><th>Event</th><th>Type</th><th>Delivery</th><th>Attempts</th><th>Next attempt</th><th>Delivered</th></tr>");
            foreach (var e in invoice.Events)
            {
                b.Append(CultureInfo.InvariantCulture, $"<tr><td class=\"mono\">{e.Id.ToString()[..8]}…</td><td>{E(e.Type)}</td><td>{E(e.DeliveryStatus.ToString())}</td><td>{e.Attempts}</td><td>{Time(e.NextAttemptAt)}</td><td>{Time(e.DeliveredAt)}</td></tr>");
            }

            b.Append("</table>");
        }
        else
        {
            b.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">{E(serviceError ?? "No invoice.")}</p>");
        }

        return Page("Order " + order.Id, "orders", b.ToString(), refreshSeconds: 3);
    }

    public static IResult WebhookLog(IReadOnlyList<WebhookLogEntry> log, CapturedWebhook? genuine, AttackResult? last)
    {
        var b = new StringBuilder("<h1>Webhook log</h1><p class=\"muted\">Everything that reached <code>POST /webhooks/acceptcrypto</code>: processed events, detected duplicates, and rejected forged / tampered / replayed requests (verified by the SDK: HMAC-SHA256, 5 min timestamp tolerance, event ID deduplication).</p>");
        b.Append("<div class=\"card\"><b>Try to fool the shop (FR-26)</b><div class=\"row\" style=\"margin-top:8px\">");
        foreach (var kind in WebhookAttacks.Kinds)
        {
            var disabled = genuine is null && kind != "forged" ? " disabled title=\"Needs a genuine webhook first\"" : string.Empty;
            b.Append(CultureInfo.InvariantCulture, $"<form method=\"post\" action=\"/demo/attacks/{kind}\"><button class=\"secondary\"{disabled}>Send {kind} webhook</button></form>");
        }

        b.Append("</div><p class=\"muted\">forged: made-up <code>invoice.paid</code> with a guessed secret · tampered: a real webhook with the status changed · replay: a real webhook resent byte for byte (duplicate within 5 min, rejected after) · stale: a real webhook captured 10 min ago. Same from a terminal: <code>scripts/webhook-attack.sh forged</code>.</p>");
        if (last is not null)
        {
            b.Append(CultureInfo.InvariantCulture, $"<div class=\"note\"><b>{E(last.Kind)}</b>: {E(last.Description)}<br>Shop answered <b>{last.StatusCode}</b> <span class=\"mono\">{E(Truncate(last.Response, 300))}</span></div>");
        }

        b.Append("</div><h2>Log (newest first)</h2>").Append(LogTable(log, showOrder: true));
        return Page("Webhooks", "webhooks", b.ToString(), refreshSeconds: last is null ? 3 : null);
    }

    public static IResult Error(string title, string message) =>
        Page(title, "shop", $"<h1>{E(title)}</h1><p>{E(message)}</p><p><a href=\"/\">Back to the shop</a></p>", statusCode: 502);

    private static string OrderTable(IEnumerable<ShopOrder> orders)
    {
        var b = new StringBuilder("<table><tr><th>Order</th><th>Product</th><th>Price</th><th>Status</th><th>Confirmed</th><th>Created</th><th></th></tr>");
        foreach (var o in orders)
        {
            b.Append(CultureInfo.InvariantCulture, $"<tr><td><a href=\"/orders/{E(o.Id)}\">{E(o.Id)}</a></td><td>{E(o.ProductName)}</td><td>{E(o.Amount)}</td><td>{Badge(o.Status)}</td><td>{E(o.ConfirmedTotal ?? "0")}</td><td>{Time(o.CreatedAt)}</td><td>");
            if (o.PaymentPageUrl is not null)
            {
                b.Append(CultureInfo.InvariantCulture, $"<a href=\"{E(o.PaymentPageUrl)}\">payment page</a>");
            }

            b.Append("</td></tr>");
        }

        return b.Append("</table>").ToString();
    }

    private static string LogTable(IReadOnlyList<WebhookLogEntry> log, bool showOrder)
    {
        if (log.Count == 0)
        {
            return "<p class=\"muted\">Nothing received yet.</p>";
        }

        var b = new StringBuilder("<table><tr><th>Received</th><th>Outcome</th><th>Event</th>");
        b.Append(showOrder ? "<th>Order</th>" : string.Empty).Append("<th>Detail</th></tr>");
        foreach (var e in log)
        {
            b.Append(CultureInfo.InvariantCulture, $"<tr><td>{Time(e.ReceivedAt)}</td><td>{Badge(e.Outcome)}");
            if (e.Attack is not null)
            {
                b.Append(CultureInfo.InvariantCulture, $" <span class=\"badge\">demo: {E(e.Attack)}</span>");
            }

            b.Append(CultureInfo.InvariantCulture, $"</td><td>{E(e.EventType ?? "–")}<br><span class=\"mono muted\">{E(e.EventId is { Length: > 8 } id ? id[..8] + "…" : e.EventId ?? string.Empty)}</span></td>");
            if (showOrder)
            {
                b.Append("<td>").Append(e.OrderId is null ? "–" : $"<a href=\"/orders/{E(e.OrderId)}\">{E(e.OrderId)}</a>").Append("</td>");
            }

            b.Append(CultureInfo.InvariantCulture, $"<td>{E(e.Detail)}</td></tr>");
        }

        return b.Append("</table>").ToString();
    }

    private static IResult Page(string title, string active, string body, int? refreshSeconds = null, int statusCode = 200)
    {
        string Nav(string href, string key, string text) => $"<a href=\"{href}\"{(key == active ? " class=\"on\"" : string.Empty)}>{text}</a>";
        var refresh = refreshSeconds is { } s ? $"<meta http-equiv=\"refresh\" content=\"{s}\">" : string.Empty;
        var html = $"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            {refresh}<title>{E(title)} · AcceptCrypto.NET demo shop</title><style>{Css}</style></head>
            <body><header><b>🛒 Demo shop</b>{Nav("/", "shop", "Shop")}{Nav("/orders", "orders", "Orders")}{Nav("/webhooks", "webhooks", "Webhook log")}</header>
            <main>{body}</main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, statusCode);
    }

    private static string Badge(string status) => $"<span class=\"badge s-{E(status)}\">{E(status)}</span>";

    private static string Time(DateTimeOffset? value) =>
        value is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "–";

    private static string Short(string hash) => hash.Length > 14 ? hash[..10] + "…" + hash[^4..] : hash;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static string E(string value) => Html.Encode(value);
}
