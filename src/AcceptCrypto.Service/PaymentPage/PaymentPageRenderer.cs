using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.PaymentPage;

/// <summary>
/// Renders the payment page HTML: the static payment facts on the server (readable without JavaScript), the live
/// status block filled in by <c>pay.js</c> from the embedded initial state and then from polling (FR-30).
/// Everything shown comes from <see cref="PublicInvoiceView"/> and is HTML-encoded.
/// </summary>
internal sealed class PaymentPageRenderer(PublicViewJson json, IOptionsMonitor<PaymentPageOptions> options)
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;

    public string Render(string token, string? sampleStatus, PublicInvoiceView view)
    {
        var o = options.CurrentValue;
        var tokenPath = Uri.EscapeDataString(token);
        var query = token == PaymentPageEndpoints.SampleToken && !string.IsNullOrEmpty(sampleStatus)
            ? "?status=" + Uri.EscapeDataString(sampleStatus)
            : string.Empty;
        var state = json.Serialize(view).Replace("<", "\\u003c", StringComparison.Ordinal);
        var merchant = string.IsNullOrWhiteSpace(o.MerchantDisplayName) ? null : o.MerchantDisplayName.Trim();
        var pollMs = ((long)o.PollInterval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        return string.Create(CultureInfo.InvariantCulture, $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <meta name="referrer" content="no-referrer">
            <title>Pay {{E(view.Amount)}} {{E(view.Token)}}{{(merchant is null ? string.Empty : " · " + E(merchant))}}</title>
            <link rel="stylesheet" href="{{PaymentPageAssets.Url("pay.css")}}">
            </head>
            <body>
            <main class="card" id="app"
              data-status-url="/api/public/invoices/{{E(tokenPath)}}{{E(query)}}"
              data-qr-url="/pay/{{E(tokenPath)}}/qr.svg"
              data-qr-query="{{E(query.TrimStart('?'))}}"
              data-poll-ms="{{pollMs}}">
              <header class="top">
                <div class="brand"><span class="logo" aria-hidden="true">◎</span>{{(merchant is null ? "Payment request" : E(merchant))}}</div>
                <span class="chip" title="Chain ID {{view.ChainId}}"><span class="dot" aria-hidden="true"></span>{{E(view.Network)}}</span>
              </header>

              <section class="status tone-wait" id="status" role="status" aria-live="polite">
                <div class="status-icon" id="status-icon" aria-hidden="true"></div>
                <div class="status-text">
                  <h1 id="status-title">{{E(StatusLabel(view))}}</h1>
                  <p id="status-message"></p>
                  <div class="progress" id="progress" hidden><div class="progress-bar" id="progress-bar"></div></div>
                </div>
              </section>
              <noscript><p class="note">Status: <strong>{{E(StatusLabel(view))}}</strong>. Reload the page to refresh it.</p></noscript>

              <section class="amount">
                <div class="label" id="amount-label">Send exactly</div>
                <div class="amount-row">
                  <div class="amount-value"><span id="amount-due">{{E(view.RemainingAmount)}}</span> <span class="unit">{{E(view.Token)}}</span></div>
                  <button type="button" class="copy" id="copy-amount" data-copy="amount-due" aria-label="Copy amount">Copy</button>
                </div>
              </section>

              <section class="pay" id="pay">
                <figure class="qr">
                  <img id="qr" src="/pay/{{E(tokenPath)}}/qr.svg{{E(query)}}" width="200" height="200" alt="QR code with the payment details">
                  <div class="qr-modes" role="group" aria-label="QR code content">
                    <button type="button" class="mode active" data-mode="uri" id="mode-uri">Wallet link</button>
                    <button type="button" class="mode" data-mode="address">Address only</button>
                  </div>
                  <figcaption id="qr-caption">Scan with a wallet that supports EIP-681 payment links.</figcaption>
                </figure>
              </section>

              <dl class="facts">
                <div class="fact">
                  <dt>Deposit address</dt>
                  <dd class="with-copy"><code class="address" id="address">{{E(view.Address)}}</code>
                    <button type="button" class="copy" data-copy="address" aria-label="Copy address">Copy</button></dd>
                </div>
                <div class="fact">
                  <dt>Network</dt>
                  <dd>{{E(view.Network)}} <span class="muted">· chain ID {{view.ChainId}}</span></dd>
                </div>
                <div class="fact">
                  <dt>Token</dt>
                  <dd>{{E(view.Token)}}{{TokenContract(view)}}</dd>
                </div>
                <div class="fact">
                  <dt>Deadline</dt>
                  <dd><time id="deadline" datetime="{{view.ExpiresAt.UtcDateTime:O}}">{{view.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm}} UTC</time>
                    <span class="countdown" id="countdown"></span></dd>
                </div>
              </dl>

              <dl class="totals" id="totals" hidden>
                <div><dt>Invoice amount</dt><dd><span id="total-amount">{{E(view.Amount)}}</span> {{E(view.Token)}}</dd></div>
                <div><dt>Received</dt><dd><span id="total-paid">{{E(view.PaidAmount)}}</span> {{E(view.Token)}}</dd></div>
                <div id="row-pending"><dt>Confirming</dt><dd><span id="total-pending">{{E(view.PendingAmount)}}</span> {{E(view.Token)}}</dd></div>
                <div id="row-remaining"><dt>Remaining</dt><dd><span id="total-remaining">{{E(view.RemainingAmount)}}</span> {{E(view.Token)}}</dd></div>
              </dl>

              <p class="note warn" id="network-warning">Send only <strong>{{E(view.Token)}}</strong> on <strong>{{E(view.Network)}}</strong>.
                Other tokens or networks cannot be detected and may be lost.</p>

              <footer class="foot">
                <span id="live" class="live"><span class="pulse" aria-hidden="true"></span><span id="live-text">Live status</span></span>
                <span>Non-custodial · AcceptCrypto.NET</span>
              </footer>
            </main>
            <script type="application/json" id="initial-state">{{state}}</script>
            <script src="{{PaymentPageAssets.Url("pay.js")}}" defer></script>
            </body>
            </html>
            """);
    }

    public static string RenderNotFound() => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <title>Payment not found</title>
        <link rel="stylesheet" href="{{PaymentPageAssets.Url("pay.css")}}">
        </head>
        <body>
        <main class="card">
          <section class="status tone-muted"><div class="status-icon" aria-hidden="true"></div>
            <div class="status-text"><h1>Payment not found</h1>
            <p>This payment link is invalid. Check the link or ask the merchant for a new one. Do not send funds.</p></div>
          </section>
        </main>
        </body>
        </html>
        """;

    private static string StatusLabel(PublicInvoiceView view) => view.Status.ToString();

    private static string TokenContract(PublicInvoiceView view) => view.TokenAddress is null
        ? string.Empty
        : $"""
           <span class="muted"> · contract</span> <code class="mono-sm" id="token-address" title="{E(view.TokenAddress)}">{E(Short(view.TokenAddress))}</code>
           """;

    private static string Short(string address) => address.Length > 14 ? $"{address[..8]}…{address[^6..]}" : address;

    private static string E(string value) => Html.Encode(value);
}
