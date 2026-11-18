// AcceptCrypto.NET payment page: live status by polling /api/public/invoices/{token} (FR-30).
(function () {
  "use strict";

  var app = document.getElementById("app");
  if (!app) { return; }

  var $ = function (id) { return document.getElementById(id); };
  var statusUrl = app.dataset.statusUrl;
  var qrUrl = app.dataset.qrUrl;
  var qrQuery = app.dataset.qrQuery || "";
  var pollMs = Math.max(500, parseInt(app.dataset.pollMs, 10) || 3000);

  var FINAL = { paid: true, overpaid: true, paid_late: true, cancelled: true };
  var state = JSON.parse($("initial-state").textContent);
  var clockOffset = 0;       // server time - local time, for the countdown
  var qrPreferred = "uri";   // the payer's choice; "address" is used while no wallet link exists
  var qrMode = "uri";
  var qrKey = "";
  var failures = 0;
  var timer = null;
  var stopped = false;

  function plural(n, word) { return n + " " + word + (n === 1 ? "" : "s"); }

  // Text and tone of the status banner. Amounts are decimal strings from the server; never computed here.
  function describe(s) {
    var t = s.token, net = s.network;
    switch (s.status) {
      case "pending":
        return s.accepting_payment
          ? { tone: "wait", title: "Waiting for payment",
              text: "Send exactly " + s.amount + " " + t + " on " + net + " to the address below. This page updates automatically." }
          : { tone: "off", title: "Deadline passed",
              text: "No payment was received in time. Do not send funds to this address; ask the merchant for a new invoice." };
      case "confirming":
        var c = s.confirmations;
        var progress = c && c.current !== null ? " Confirmations: " + c.current + " of " + c.required + "." : " Waiting for confirmations.";
        return { tone: "prog", title: "Payment detected — confirming",
                 text: s.pending_amount + " " + t + " seen on-chain." + progress + " Keep this page open or come back later." };
      case "underpaid":
        return s.accepting_payment
          ? { tone: "warn", title: "Partially paid",
              text: "Received " + s.paid_amount + " of " + s.amount + " " + t + ". Send the remaining " + s.remaining_amount + " " + t + " before the deadline." }
          : { tone: "off", title: "Deadline passed",
              text: "Received " + s.paid_amount + " of " + s.amount + " " + t + ". Please contact the merchant." };
      case "paid":
        return { tone: "ok", title: "Payment received",
                 text: s.paid_amount + " " + t + " confirmed. Thank you — you can close this page." };
      case "overpaid":
        return { tone: "ok", title: "Payment received (overpaid)",
                 text: s.paid_amount + " " + t + " confirmed, " + s.excess_amount + " " + t + " more than requested. The merchant decides how to handle the difference." };
      case "paid_late":
        return { tone: "warn", title: "Paid after the deadline",
                 text: s.paid_amount + " " + t + " confirmed after the deadline. The merchant decides whether to accept it." };
      case "expired":
        return { tone: "off", title: "Invoice expired",
                 text: s.paid_amount !== "0"
                   ? "Received " + s.paid_amount + " of " + s.amount + " " + t + " before the deadline. Please contact the merchant."
                   : "The payment deadline passed. Do not send funds to this address; ask the merchant for a new invoice." };
      case "cancelled":
        return { tone: "off", title: "Invoice cancelled",
                 text: "The merchant cancelled this invoice. Do not send funds to this address." };
      default:
        return { tone: "wait", title: s.status, text: "" };
    }
  }

  function render(s) {
    var d = describe(s);
    $("status").className = "status tone-" + d.tone;
    $("status-title").textContent = d.title;
    $("status-message").textContent = d.text;

    var c = s.confirmations;
    var showProgress = s.status === "confirming" && c && c.required > 0;
    $("progress").hidden = !showProgress;
    if (showProgress) {
      var pct = c.current === null ? 8 : Math.max(8, Math.round(100 * c.current / c.required));
      $("progress-bar").style.width = pct + "%";
    }

    var accepting = !!s.accepting_payment;
    app.classList.toggle("closed", !accepting && s.status !== "confirming");
    $("pay").hidden = !accepting;
    $("network-warning").hidden = !accepting;
    $("copy-amount").hidden = !accepting;
    $("amount-label").textContent = accepting
      ? (s.status === "underpaid" ? "Send the remaining" : "Send exactly")
      : "Invoice amount";
    $("amount-due").textContent = accepting ? s.remaining_amount : s.amount;

    var partial = s.paid_amount !== "0" || s.pending_amount !== "0";
    $("totals").hidden = !partial;
    $("total-amount").textContent = s.amount;
    $("total-paid").textContent = s.paid_amount;
    $("total-pending").textContent = s.pending_amount;
    $("total-remaining").textContent = s.remaining_amount;
    $("row-pending").hidden = s.pending_amount === "0";
    $("row-remaining").hidden = s.remaining_amount === "0";

    // Wallet-link QR only when an EIP-681 URI exists (token address known, payment expected).
    $("mode-uri").hidden = !s.payment_uri;
    setMode(s.payment_uri ? qrPreferred : "address");
    refreshQr(s);
    tick();
  }

  function refreshQr(s) {
    var key = qrMode + "|" + (qrMode === "uri" ? s.payment_uri : s.address);
    if (key === qrKey || $("pay").hidden) { return; }
    qrKey = key;
    var q = "mode=" + qrMode + "&v=" + encodeURIComponent(s.remaining_amount) + (qrQuery ? "&" + qrQuery : "");
    $("qr").src = qrUrl + "?" + q;
    $("qr-caption").textContent = qrMode === "uri"
      ? "Scan with a wallet that supports EIP-681 payment links (amount and token pre-filled)."
      : "Address only: enter the amount and select " + s.token + " on " + s.network + " in your wallet.";
  }

  function setMode(mode) {
    qrMode = mode;
    var buttons = document.querySelectorAll(".mode");
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].classList.toggle("active", buttons[i].dataset.mode === mode);
    }
  }

  // Deadline countdown, corrected for the client clock.
  function tick() {
    var el = $("countdown");
    var deadline = new Date(state.expires_at);
    $("deadline").textContent = deadline.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
    if (!state.accepting_payment) { el.textContent = ""; return; }
    var left = Math.floor((deadline.getTime() - (Date.now() + clockOffset)) / 1000);
    if (left <= 0) { el.textContent = "· deadline reached"; el.className = "countdown soon"; return; }
    var h = Math.floor(left / 3600), m = Math.floor(left % 3600 / 60), sec = left % 60;
    var pad = function (n) { return (n < 10 ? "0" : "") + n; };
    el.textContent = "· " + (h > 0 ? h + ":" + pad(m) : m) + ":" + pad(sec) + " left";
    el.className = left < 300 ? "countdown soon" : "countdown";
  }

  function setLive(kind, text) {
    $("live").className = "live " + kind;
    $("live-text").textContent = text;
  }

  function schedule(ms) {
    clearTimeout(timer);
    if (!stopped) { timer = setTimeout(poll, ms); }
  }

  function nextDelay() {
    if (failures > 0) { return Math.min(30000, pollMs * Math.pow(2, failures)); }
    return state.status === "expired" ? Math.max(pollMs, 10000) : pollMs;  // expired: a late payment is still possible
  }

  function poll() {
    if (document.hidden) { return; }  // resumed by visibilitychange
    fetch(statusUrl, { cache: "no-store", headers: { Accept: "application/json" } })
      .then(function (r) {
        if (r.status === 404) { stopped = true; setLive("stopped", "Invoice not found"); return null; }
        if (!r.ok) { throw new Error("HTTP " + r.status); }
        return r.json();
      })
      .then(function (s) {
        if (!s) { return; }
        failures = 0;
        apply(s);
        if (FINAL[s.status]) { stopped = true; setLive("stopped", "Final status"); return; }
        setLive("", "Live status");
        schedule(nextDelay());
      })
      .catch(function () {
        failures++;
        setLive("offline", "Reconnecting…");
        schedule(nextDelay());
      });
  }

  function apply(s) {
    clockOffset = new Date(s.server_time).getTime() - Date.now();
    state = s;
    render(s);
  }

  // Copy buttons with feedback; falls back to selecting the text where the Clipboard API is unavailable.
  function copy(button) {
    var target = $(button.dataset.copy);
    var text = target.textContent.trim();
    var done = function () {
      var label = button.textContent;
      button.textContent = "Copied";
      button.classList.add("done");
      setTimeout(function () { button.textContent = label === "Copied" ? "Copy" : label; button.classList.remove("done"); }, 1400);
    };
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(text).then(done, function () { select(target); });
    } else {
      select(target);
      try { if (document.execCommand("copy")) { done(); } } catch (e) { /* text stays selected */ }
    }
  }

  function select(el) {
    var range = document.createRange();
    range.selectNodeContents(el);
    var sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
  }

  document.addEventListener("click", function (e) {
    var b = e.target.closest("button");
    if (!b) { return; }
    if (b.dataset.copy) { copy(b); }
    if (b.dataset.mode) { qrPreferred = b.dataset.mode; setMode(qrPreferred); refreshQr(state); }
  });

  document.addEventListener("visibilitychange", function () {
    if (!document.hidden && !stopped) { schedule(0); }
  });

  apply(state);
  setInterval(tick, 1000);
  if (FINAL[state.status]) { stopped = true; setLive("stopped", "Final status"); } else { schedule(pollMs); }
})();
