using AcceptCrypto.Domain.Invoices;

namespace AcceptCrypto.Domain.Tests;

public class InvoiceStatusTests
{
    [Theory]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Overpaid)]
    [InlineData(InvoiceStatus.PaidLate)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void TerminalStatusesAreTerminal(InvoiceStatus status) => Assert.True(status.IsTerminal());

    [Theory]
    [InlineData(InvoiceStatus.Pending)]
    [InlineData(InvoiceStatus.Confirming)]
    [InlineData(InvoiceStatus.Underpaid)]
    [InlineData(InvoiceStatus.Expired)]
    public void OpenStatusesAreNotTerminal(InvoiceStatus status) => Assert.False(status.IsTerminal());
}
