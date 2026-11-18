using AcceptCrypto.DemoShop.Shop;
using AcceptCrypto.Sdk;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.Extensions.Options;

// AcceptCrypto.NET demo shop: a tiny catalog that takes USDC through the SDK.
// 1. Checkout creates an invoice (IAcceptCryptoClient) and redirects to the service's payment page.
// 2. MapAcceptCryptoWebhook verifies, parses and deduplicates webhooks and updates the order.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAcceptCrypto();   // section "AcceptCrypto": BaseUrl, ApiKey, WebhookSecret
builder.Services.Configure<ShopOptions>(builder.Configuration.GetSection(ShopOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ShopStore>();
builder.Services.AddSingleton<IWebhookEventDeduplicator>(sp => sp.GetRequiredService<ShopStore>());   // persistent dedup (FR-16)
builder.Services.AddSingleton<IAcceptCryptoWebhookObserver>(sp => sp.GetRequiredService<ShopStore>()); // log rejected/duplicates
builder.Services.AddSingleton<WebhookAttacks>();
builder.Services.AddHttpClient("attacks");
builder.Services.AddHealthChecks();

var app = builder.Build();
app.Services.GetRequiredService<ShopStore>(); // load the data file at startup

app.MapHealthChecks("/health");

app.MapGet("/", (ShopStore store) => Pages.Catalog(store.Orders()));

app.MapPost("/checkout", async (HttpRequest request, ShopStore store, IAcceptCryptoClient acceptCrypto, TimeProvider time, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var product = Catalog.Find(form["product"]);
    if (product is null)
    {
        return Results.BadRequest("Unknown product.");
    }

    var order = new ShopOrder
    {
        Id = "ORD-" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 4)),
        ProductId = product.Id,
        ProductName = product.Name,
        Amount = product.Price,
        CreatedAt = time.GetUtcNow(),
        Status = OrderStatus.Creating,
    };
    store.SaveOrder(order);

    try
    {
        var invoice = await acceptCrypto.CreateInvoiceAsync(
            new CreateInvoiceRequest
            {
                Amount = UsdcAmount.Parse(product.Price),
                ExternalOrderId = order.Id,
                Metadata = new Dictionary<string, string> { ["product"] = product.Id },
                ExpiresIn = int.TryParse(form["lifetime"], out var minutes) ? TimeSpan.FromMinutes(minutes) : null,
            },
            idempotencyKey: "order-" + order.Id, // a retried checkout never creates a second invoice
            ct);

        store.SaveOrder(order with
        {
            InvoiceId = invoice.Id,
            Status = OrderStatus.FromInvoice(invoice.Status),
            PaymentPageUrl = invoice.PaymentPageUrl.ToString(),
            DepositAddress = invoice.Payment.Address,
            ExpiresAt = invoice.ExpiresAt,
            ConfirmedTotal = invoice.ConfirmedTotal.ToString(),
        });
        return Results.Redirect(invoice.PaymentPageUrl.ToString());
    }
    catch (AcceptCryptoException ex)
    {
        store.SaveOrder(order with { Status = OrderStatus.InvoiceFailed, Error = ex.Message });
        return Pages.Error("Payment service unavailable", $"Order {order.Id}: {ex.Message}");
    }
});

app.MapGet("/orders", (ShopStore store) => Pages.OrderList(store.Orders()));
app.MapGet("/api/orders", (ShopStore store) => Results.Json(store.Orders())); // for scripts (smoke test)

app.MapGet("/orders/{id}", async (string id, ShopStore store, IAcceptCryptoClient acceptCrypto, IOptions<ShopOptions> options, CancellationToken ct) =>
{
    var order = store.FindOrder(id);
    if (order is null)
    {
        return Results.NotFound();
    }

    InvoiceDetails? invoice = null;
    string? serviceError = null;
    if (order.InvoiceId is { } invoiceId)
    {
        try
        {
            invoice = await acceptCrypto.GetInvoiceAsync(invoiceId, ct);
        }
        catch (AcceptCryptoException ex)
        {
            serviceError = "Payment service not reachable: " + ex.Message;
        }
    }

    return Pages.Order(order, store.WebhookLog(order.Id), invoice, serviceError, options.Value.PayerCommand);
});

app.MapGet("/webhooks", (ShopStore store) => Pages.WebhookLog(store.WebhookLog(), store.LastGenuineWebhook(), null));

// The webhook receiver: signature + timestamp verification, typed event, deduplication — all in the SDK.
app.MapAcceptCryptoWebhook(WebhookAttacks.WebhookPath, (AcceptCryptoEvent e, HttpContext context) =>
{
    context.RequestServices.GetRequiredService<ShopStore>().ApplyEvent(e, context);
    return Task.CompletedTask;
});

// FR-26 demo: send a forged / tampered / replayed / stale webhook to the endpoint above.
app.MapPost("/demo/attacks/{kind}", async (string kind, HttpRequest request, WebhookAttacks attacks, ShopStore store, CancellationToken ct) =>
{
    if (!WebhookAttacks.Kinds.Contains(kind))
    {
        return Results.NotFound();
    }

    var result = await attacks.RunAsync(kind, ct);
    return request.HasFormContentType
        ? Pages.WebhookLog(store.WebhookLog(), store.LastGenuineWebhook(), result)
        : Results.Json(result);
});

app.Run();
