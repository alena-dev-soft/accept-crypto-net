using System.Globalization;
using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using Microsoft.Extensions.Time.Testing;

namespace AcceptCrypto.Domain.Tests;

/// <summary>Builds invoices and transfers on a fake clock. Default invoice: 10 USDC, 30 min lifetime.</summary>
internal sealed class TestKit
{
    public static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    public const string DepositAddress = "0x1111111111111111111111111111111111111111";
    public const string PayerAddress = "0x2222222222222222222222222222222222222222";

    private int _txCounter;

    public FakeTimeProvider Clock { get; } = new(Start);

    public InvoicePolicy Policy { get; } = new();

    public DateTimeOffset Now => Clock.GetUtcNow();

    public Invoice NewInvoice(string amount = "10", TimeSpan? lifetime = null) =>
        Invoice.Create(new NewInvoice(UsdcAmount.Parse(amount), "order-1", DepositAddress, 0, Lifetime: lifetime), Policy, Clock);

    /// <summary>A transfer to the deposit address; the block timestamp defaults to the current fake time.</summary>
    public ChainTransfer Transfer(string amount, DateTimeOffset? blockTime = null, string to = DepositAddress)
    {
        var n = ++_txCounter;
        return new ChainTransfer(
            Hash(n), 0, 100 + n, Hash(1000 + n), blockTime ?? Now, PayerAddress, to, UsdcAmount.Parse(amount));
    }

    public static string Hash(int n) => "0x" + n.ToString("x64", CultureInfo.InvariantCulture);

    /// <summary>Records a transfer, asserting it was accepted, and returns the status types of the events.</summary>
    public static string[] Record(Invoice invoice, ChainTransfer transfer, DateTimeOffset now)
    {
        var result = invoice.RecordTransfer(transfer, now);
        Assert.Equal(TransferRecordOutcome.Accepted, result.Outcome);
        return Types(result.Events);
    }

    public static string[] Types(IEnumerable<InvoiceDomainEvent> events) => events.Select(e => e.Type).ToArray();
}
