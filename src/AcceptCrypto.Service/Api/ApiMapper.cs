using System.Buffers.Text;
using System.Globalization;
using System.Text;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Infrastructure.Chain.Scanning;
using AcceptCrypto.Infrastructure.Persistence;
using AcceptCrypto.Service.Configuration;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.Api;

/// <summary>Maps domain/persistence objects to API contracts.</summary>
internal sealed class ApiMapper(IOptionsMonitor<ChainOptions> chain, IOptionsMonitor<ApiOptions> api, ChainScanState scanState)
{
    public const string Currency = "USDC";
    public const int Decimals = 6;

    public InvoiceResponse ToResponse(Invoice invoice, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new InvoiceResponse(
            invoice.Id, invoice.Status, invoice.Amount.ToString(), Currency, invoice.ExternalOrderId, invoice.Metadata,
            invoice.ConfirmedTotal.ToString(), invoice.UnconfirmedTotal.ToString(), invoice.RemainingAmount.ToString(),
            invoice.ExcessAmount.ToString(), invoice.CreatedAt, invoice.ExpiresAt, invoice.ExpiredAt, invoice.UpdatedAt,
            PaymentPageUrl(invoice, request), PaymentDetails(invoice));
    }

    public InvoiceDetailsResponse ToDetails(Invoice invoice, IReadOnlyList<OutboxEvent> events, HttpRequest request)
    {
        var r = ToResponse(invoice, request);
        var head = scanState.Head;
        var transfers = invoice.Transfers
            .OrderBy(t => t.BlockNumber).ThenBy(t => t.LogIndex)
            .Select(t => new TransferResponse(
                t.TxHash, t.LogIndex, t.From, t.Amount.ToString(), t.BlockNumber, t.BlockHash, t.BlockTimestamp, t.IsOnTime, t.State,
                head is { } h && t.State != TransferState.Reverted ? Math.Max(0, h - t.BlockNumber + 1) : null,
                t.SeenAt, t.ConfirmedAt, t.RevertedAt))
            .ToList();
        return new InvoiceDetailsResponse(
            r.Id, r.Status, r.Amount, r.Currency, r.ExternalOrderId, r.Metadata, r.ConfirmedTotal, r.UnconfirmedTotal,
            r.RemainingAmount, r.ExcessAmount, r.CreatedAt, r.ExpiresAt, r.ExpiredAt, r.UpdatedAt, r.PaymentPageUrl, r.Payment,
            transfers, events.Select(ToResponse).ToList());
    }

    public static EventResponse ToResponse(OutboxEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new EventResponse(
            e.EventId, e.Type, e.InvoiceId, e.Status, e.Attempts, e.CreatedAt,
            e.Status == OutboxEventStatus.Pending ? e.NextAttemptAt : null, e.DeliveredAt, e.LastError,
            e.DeliveryAttempts.OrderBy(a => a.AttemptedAt)
                .Select(a => new DeliveryAttemptResponse(a.AttemptedAt, a.HttpStatus, a.Error, a.DurationMs)).ToList());
    }

    public static UnmatchedPaymentResponse ToResponse(UnmatchedPayment u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new UnmatchedPaymentResponse(
            u.Id, u.ChainId, u.TxHash, u.LogIndex, u.BlockNumber, u.BlockHash, u.BlockTimestamp, u.FromAddress, u.ToAddress,
            u.Amount.ToString(), u.Reason, u.InvoiceId, u.RecordedAt);
    }

    private PaymentDetails PaymentDetails(Invoice invoice)
    {
        var c = chain.CurrentValue;
        return new PaymentDetails(
            c.NetworkName, c.ChainId, c.TokenSymbol, c.TokenAddress, Decimals, invoice.DepositAddress, invoice.Amount.ToString(),
            invoice.RemainingAmount.ToString(), invoice.ExpiresAt);
    }

    private Uri PaymentPageUrl(Invoice invoice, HttpRequest request)
    {
        var configured = api.CurrentValue.PublicBaseUrl;
        var root = configured is not null
            ? configured.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
        return new Uri($"{root}/pay/{invoice.PublicToken}");
    }
}

/// <summary>Opaque cursors (base64url); the content is an implementation detail clients must not parse.</summary>
internal static class Cursors
{
    public static string Encode(InvoiceCursor c) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes(
            $"i1:{c.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{c.Id:N}"));

    public static bool TryDecode(string value, out InvoiceCursor? cursor)
    {
        cursor = null;
        var parts = TryText(value)?.Split(':');
        if (parts is not ["i1", var ticks, var id]
            || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
            || t < DateTimeOffset.MinValue.UtcTicks || t > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(id, "N", out var g))
        {
            return false;
        }

        cursor = new InvoiceCursor(new DateTimeOffset(t, TimeSpan.Zero), g);
        return true;
    }

    public static string Encode(long unmatchedId) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes($"u1:{unmatchedId.ToString(CultureInfo.InvariantCulture)}"));

    public static bool TryDecode(string value, out long unmatchedId)
    {
        unmatchedId = 0;
        var parts = TryText(value)?.Split(':');
        return parts is ["u1", var id]
            && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out unmatchedId)
            && unmatchedId > 0;
    }

    private static string? TryText(string value)
    {
        if (value.Length > 200)
        {
            return null;
        }

        try
        {
            return Encoding.ASCII.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
