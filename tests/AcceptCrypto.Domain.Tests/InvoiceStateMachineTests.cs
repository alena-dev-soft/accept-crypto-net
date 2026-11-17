using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using static AcceptCrypto.Domain.Invoices.InvoiceEventTypes;
using static AcceptCrypto.Domain.Tests.TestKit;

namespace AcceptCrypto.Domain.Tests;

/// <summary>
/// Every edge of the state diagram (spec + D5/D6/D11), plus the explicit edge cases.
/// Default invoice: 10 USDC, deadline = Start + 30 min.
/// </summary>
public class InvoiceStateMachineTests
{
    private const string Reverted = InvoiceEventTypes.PaymentReverted;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset Deadline = Start + Lifetime;
    private static readonly DateTimeOffset BeforeDeadline = Deadline - TimeSpan.FromSeconds(1);

    private readonly TestKit _kit = new();
    private readonly Invoice _invoice;

    public InvoiceStateMachineTests() => _invoice = _kit.NewInvoice("10");

    private DateTimeOffset AfterDeadline()
    {
        _kit.Clock.SetUtcNow(Deadline + TimeSpan.FromMinutes(5));
        return _kit.Now;
    }

    private string[] Pay(string amount, DateTimeOffset? blockTime = null, bool confirm = true)
    {
        var transfer = _kit.Transfer(amount, blockTime);
        var types = Record(_invoice, transfer, _kit.Now).ToList();
        if (confirm)
        {
            types.AddRange(Types(_invoice.ConfirmTransfer(transfer.Id, _kit.Now)));
        }

        return [.. types];
    }

    // ---- The diagram, edge by edge -------------------------------------------------------------

    [Fact]
    public void Pending_To_Confirming_WhenPaymentSeen()
    {
        Assert.Equal([Confirming], Pay("10", confirm: false));
        Assert.Equal(InvoiceStatus.Confirming, _invoice.Status);
        Assert.Equal(UsdcAmount.Parse("10"), _invoice.UnconfirmedTotal);
        Assert.Equal(UsdcAmount.Zero, _invoice.ConfirmedTotal);
    }

    [Fact]
    public void Confirming_To_Paid_WhenConfirmedTotalEqualsAmount()
    {
        Assert.Equal([Confirming, Paid], Pay("10"));
        Assert.Equal(InvoiceStatus.Paid, _invoice.Status);
        Assert.Equal(UsdcAmount.Zero, _invoice.RemainingAmount);
    }

    [Fact]
    public void Confirming_To_Overpaid_WhenConfirmedTotalExceedsAmount()
    {
        Assert.Equal([Confirming, Overpaid], Pay("10.000001"));
        Assert.Equal(UsdcAmount.Parse("0.000001"), _invoice.ExcessAmount);
    }

    [Fact]
    public void Confirming_To_Underpaid_WhenConfirmedTotalBelowAmount()
    {
        Assert.Equal([Confirming, Underpaid], Pay("9.999999"));
        Assert.Equal(UsdcAmount.Parse("0.000001"), _invoice.RemainingAmount);
    }

    [Fact]
    public void Underpaid_To_Confirming_ToPaid_PartialPaymentsAddUp()
    {
        Pay("4");
        Assert.Equal([Confirming], Pay("6", confirm: false));
        Assert.Equal(InvoiceStatus.Confirming, _invoice.Status);

        Assert.Equal([Paid], Types(_invoice.ConfirmTransfer(_invoice.Transfers[1].Id, _kit.Now)));
        Assert.Equal(UsdcAmount.Parse("10"), _invoice.ConfirmedTotal);
    }

    [Fact]
    public void ThreePartialPayments_EndOverpaid()
    {
        Pay("3");
        Pay("3");
        Assert.Equal(InvoiceStatus.Underpaid, _invoice.Status);
        Assert.Equal([Confirming, Overpaid], Pay("5"));
        Assert.Equal(UsdcAmount.Parse("1"), _invoice.ExcessAmount);
    }

    [Fact]
    public void Confirming_To_Pending_OnReorg_WhenNothingLeft()
    {
        var t = _kit.Transfer("10");
        Record(_invoice, t, _kit.Now);

        var events = _invoice.RevertTransfer(t.Id, _kit.Now);

        Assert.Equal([Reverted, InvoiceEventTypes.Pending], Types(events));
        Assert.Equal(InvoiceStatus.Pending, _invoice.Status);
        var reverted = Assert.IsType<PaymentReverted>(events[0]);
        Assert.Equal(t.Id, reverted.Transfer);
        Assert.Equal(t.Amount, reverted.TransferAmount);
        Assert.Equal(t.BlockHash, reverted.BlockHash);
        Assert.Equal(TransferState.Reverted, _invoice.Transfers[0].State);
    }

    [Fact]
    public void Confirming_To_Underpaid_OnReorg_WhenPartialLeft()
    {
        Pay("4");
        var t = _kit.Transfer("6");
        Record(_invoice, t, _kit.Now);

        Assert.Equal([Reverted, Underpaid], Types(_invoice.RevertTransfer(t.Id, _kit.Now)));
        Assert.Equal(UsdcAmount.Parse("4"), _invoice.ConfirmedTotal);
    }

    [Fact]
    public void Reorg_NeverProducesPaid()
    {
        var t = _kit.Transfer("10");
        Record(_invoice, t, _kit.Now);
        _invoice.RevertTransfer(t.Id, _kit.Now);

        Assert.NotEqual(InvoiceStatus.Paid, _invoice.Status);
        Assert.Throws<DomainException>(() => _invoice.ConfirmTransfer(t.Id, _kit.Now));
        Assert.Equal(InvoiceStatus.Pending, _invoice.Status);
    }

    [Fact]
    public void Reorg_OneOfTwoSeen_StaysConfirming()
    {
        var a = _kit.Transfer("5");
        var b = _kit.Transfer("5");
        Record(_invoice, a, _kit.Now);
        Record(_invoice, b, _kit.Now);

        Assert.Equal([Reverted], Types(_invoice.RevertTransfer(a.Id, _kit.Now)));
        Assert.Equal(InvoiceStatus.Confirming, _invoice.Status);
    }

    [Fact]
    public void Pending_To_Expired_AtDeadline()
    {
        _kit.Clock.SetUtcNow(BeforeDeadline);
        Assert.Empty(_invoice.CheckDeadline(_kit.Now));
        Assert.Equal(InvoiceStatus.Pending, _invoice.Status);

        _kit.Clock.SetUtcNow(Deadline); // the deadline itself is no longer on time
        var events = _invoice.CheckDeadline(_kit.Now);

        Assert.Equal([Expired], Types(events));
        Assert.Equal(Deadline, _invoice.ExpiredAt);
        Assert.Empty(_invoice.CheckDeadline(_kit.Now)); // idempotent
    }

    [Fact]
    public void Underpaid_To_Expired_AtDeadline_KeepsPartialAmount()
    {
        Pay("4");
        Assert.Equal([Expired], Types(_invoice.CheckDeadline(AfterDeadline())));
        Assert.Equal(UsdcAmount.Parse("4"), _invoice.ConfirmedTotal);
        Assert.Equal(UsdcAmount.Parse("6"), _invoice.RemainingAmount);
    }

    [Fact]
    public void Expired_To_PaidLate_WhenLatePaymentConfirmed()
    {
        _invoice.CheckDeadline(AfterDeadline());

        Assert.Empty(Pay("10", confirm: false)); // a late payment does not reopen the invoice
        Assert.Equal(InvoiceStatus.Expired, _invoice.Status);
        Assert.False(_invoice.Transfers[0].IsOnTime);

        Assert.Equal([PaidLate], Types(_invoice.ConfirmTransfer(_invoice.Transfers[0].Id, _kit.Now)));
    }

    [Fact]
    public void Expired_PartialOnTime_PlusLateRest_PaidLate()
    {
        Pay("4");
        _invoice.CheckDeadline(AfterDeadline());
        Assert.Equal([PaidLate], Pay("6"));
        Assert.Equal(UsdcAmount.Parse("4"), _invoice.ConfirmedOnTimeTotal);
        Assert.Equal(UsdcAmount.Parse("10"), _invoice.ConfirmedTotal);
    }

    [Fact]
    public void Pending_To_Cancelled()
    {
        var events = _invoice.Cancel(_kit.Now);
        Assert.Equal([Cancelled], Types(events));
        Assert.Equal(InvoiceStatus.Cancelled, _invoice.Status);
        Assert.True(_invoice.Status.IsTerminal());
    }

    [Fact]
    public void Cancel_FromNonPending_Rejected()
    {
        Pay("10", confirm: false);
        var ex = Assert.Throws<DomainException>(() => _invoice.Cancel(_kit.Now));
        Assert.Equal(DomainErrorCodes.InvalidTransition, ex.Code);
        Assert.Equal(InvoiceStatus.Confirming, _invoice.Status);
    }

    [Theory]
    [InlineData(InvoiceStatus.Underpaid)]
    [InlineData(InvoiceStatus.Expired)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Overpaid)]
    [InlineData(InvoiceStatus.PaidLate)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Cancel_FromEveryOtherStatus_Rejected(InvoiceStatus status)
    {
        DriveTo(status);
        Assert.Equal(status, _invoice.Status);
        if (status == InvoiceStatus.Cancelled)
        {
            Assert.Throws<DomainException>(() => _invoice.Cancel(_kit.Now));
            return;
        }

        Assert.Equal(DomainErrorCodes.InvalidTransition, Assert.Throws<DomainException>(() => _invoice.Cancel(_kit.Now)).Code);
        Assert.Equal(status, _invoice.Status);
    }

    // ---- Deadlines and timing (block timestamp decides) -----------------------------------------

    [Fact]
    public void Confirming_IsNotExpired_AtDeadline_AndOnTimePaymentConfirmedLaterIsPaid()
    {
        Pay("10", blockTime: BeforeDeadline, confirm: false);

        Assert.Empty(_invoice.CheckDeadline(AfterDeadline()));
        Assert.Equal(InvoiceStatus.Confirming, _invoice.Status);

        Assert.Equal([Paid], Types(_invoice.ConfirmTransfer(_invoice.Transfers[0].Id, _kit.Now)));
    }

    [Fact]
    public void OnTimePayment_FoundAfterDowntime_BeforeExpiryRan_IsPaid()
    {
        AfterDeadline(); // wall clock past the deadline, invoice not yet expired by the worker
        Assert.Equal([Confirming, Paid], Pay("10", blockTime: BeforeDeadline));
    }

    [Fact]
    public void OnTimePayment_FoundAfterInvoiceExpired_ReopensToConfirming_ThenPaid()
    {
        _invoice.CheckDeadline(AfterDeadline());
        Assert.Equal(InvoiceStatus.Expired, _invoice.Status);

        Assert.Equal([Confirming, Paid], Pay("10", blockTime: BeforeDeadline)); // D11: expired → confirming
    }

    [Fact]
    public void LatePayment_OnPendingNotYetExpired_ExpiresFirst()
    {
        AfterDeadline();
        Assert.Equal([Expired], Pay("10", confirm: false)); // block timestamp after the deadline = chain evidence
        Assert.Equal([PaidLate], Types(_invoice.ConfirmTransfer(_invoice.Transfers[0].Id, _kit.Now)));
    }

    [Fact]
    public void BlockTimestampExactlyAtDeadline_IsLate()
    {
        AfterDeadline();
        Assert.Equal([Expired, PaidLate], Pay("10", blockTime: Deadline));
    }

    [Fact]
    public void Confirming_LastPartialConfirmedAfterDeadline_UnderpaidThenExpired()
    {
        Pay("4", blockTime: BeforeDeadline, confirm: false);
        AfterDeadline();

        var events = _invoice.ConfirmTransfer(_invoice.Transfers[0].Id, _kit.Now);

        Assert.Equal([Underpaid, Expired], Types(events));
        Assert.Equal([InvoiceStatus.Confirming, InvoiceStatus.Underpaid], events.Cast<InvoiceStatusChanged>().Select(e => e.PreviousStatus));
    }

    [Fact]
    public void Confirming_ReorgAfterDeadline_PendingThenExpired()
    {
        var t = _kit.Transfer("10", BeforeDeadline);
        Record(_invoice, t, _kit.Now);
        AfterDeadline();

        Assert.Equal([Reverted, InvoiceEventTypes.Pending, Expired], Types(_invoice.RevertTransfer(t.Id, _kit.Now)));
    }

    [Fact]
    public void Confirming_OnTimeReverted_LateAlreadyConfirmed_WalksToPaidLate()
    {
        var onTime = _kit.Transfer("5", BeforeDeadline);
        Record(_invoice, onTime, _kit.Now);
        AfterDeadline();
        Pay("10"); // late, confirmed while the invoice is still confirming → no status change yet
        Assert.Equal(InvoiceStatus.Confirming, _invoice.Status);

        var events = _invoice.RevertTransfer(onTime.Id, _kit.Now);

        Assert.Equal([Reverted, InvoiceEventTypes.Pending, Expired, PaidLate], Types(events));
    }

    [Fact]
    public void OverpaymentAfterDeadline_IsPaidLate_WithExcessVisible()
    {
        _invoice.CheckDeadline(AfterDeadline());
        Assert.Equal([PaidLate], Pay("12"));
        Assert.Equal(UsdcAmount.Parse("2"), _invoice.ExcessAmount);
    }

    [Fact]
    public void PartialLatePayment_IntoExpired_StaysExpired_AndAddsUp()
    {
        _invoice.CheckDeadline(AfterDeadline());
        Assert.Empty(Pay("3"));
        Assert.Equal(InvoiceStatus.Expired, _invoice.Status);
        Assert.Equal(UsdcAmount.Parse("3"), _invoice.ConfirmedTotal);
        Assert.Equal([PaidLate], Pay("7"));
    }

    [Fact]
    public void ExpiredIsSticky_EvenIfClockGoesBack()
    {
        _invoice.CheckDeadline(AfterDeadline());
        Assert.Empty(_invoice.CheckDeadline(BeforeDeadline)); // e.g. a skewed node clock
        Assert.Equal(InvoiceStatus.Expired, _invoice.Status);
    }

    [Fact]
    public void PaidStaysPaid_AfterDeadline() // terminal statuses never expire
    {
        Pay("10");
        Assert.Empty(_invoice.CheckDeadline(AfterDeadline()));
        Assert.Equal(InvoiceStatus.Paid, _invoice.Status);
    }

    // ---- Transfers: duplicates, closed invoices, reorg details --------------------------------

    [Fact]
    public void SameTransferTwice_IsCountedOnce()
    {
        var t = _kit.Transfer("10");
        Record(_invoice, t, _kit.Now);
        var again = _invoice.RecordTransfer(t, _kit.Now);

        Assert.Equal(TransferRecordOutcome.Duplicate, again.Outcome);
        Assert.Empty(again.Events);
        Assert.Single(_invoice.Transfers);

        _invoice.ConfirmTransfer(t.Id, _kit.Now);
        Assert.Empty(_invoice.ConfirmTransfer(t.Id, _kit.Now)); // confirm is idempotent
        Assert.Equal(TransferRecordOutcome.Duplicate, _invoice.RecordTransfer(t, _kit.Now).Outcome); // even when terminal
        Assert.Equal(UsdcAmount.Parse("10"), _invoice.ConfirmedTotal);
        Assert.Equal(InvoiceStatus.Paid, _invoice.Status);
    }

    [Theory]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Overpaid)]
    [InlineData(InvoiceStatus.PaidLate)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void TransferToTerminalInvoice_IsNotAccepted(InvoiceStatus terminal)
    {
        DriveTo(terminal);
        var count = _invoice.Transfers.Count;

        var result = _invoice.RecordTransfer(_kit.Transfer("1"), _kit.Now);

        Assert.Equal(TransferRecordOutcome.InvoiceClosed, result.Outcome);
        Assert.Empty(result.Events);
        Assert.Equal(count, _invoice.Transfers.Count);
        Assert.Equal(terminal, _invoice.Status);
    }

    [Fact]
    public void TransferToOtherAddress_Rejected()
    {
        var t = _kit.Transfer("10", to: "0x3333333333333333333333333333333333333333");
        Assert.Equal(DomainErrorCodes.TransferAddressMismatch, Assert.Throws<DomainException>(() => _invoice.RecordTransfer(t, _kit.Now)).Code);
    }

    [Fact]
    public void RevertedTransfer_ReincludedInNewBlock_IsReinstated()
    {
        var t = _kit.Transfer("10");
        Record(_invoice, t, _kit.Now);
        _invoice.RevertTransfer(t.Id, _kit.Now);
        var reincluded = new Chain.ChainTransfer(t.Id.TxHash, t.Id.LogIndex, t.BlockNumber + 1, Hash(9999), t.BlockTimestamp, t.From, t.To, t.Amount);

        Assert.Equal([Confirming], Record(_invoice, reincluded, _kit.Now));
        Assert.Single(_invoice.Transfers);
        Assert.Equal(Hash(9999), _invoice.Transfers[0].BlockHash);
        Assert.Null(_invoice.Transfers[0].RevertedAt);
        Assert.Equal([Paid], Types(_invoice.ConfirmTransfer(t.Id, _kit.Now)));
    }

    [Fact]
    public void RevertingConfirmedTransfer_Rejected_AndRevertIsIdempotent()
    {
        Pay("4");
        var confirmed = _invoice.Transfers[0].Id;
        Assert.Equal(DomainErrorCodes.TransferStateConflict, Assert.Throws<DomainException>(() => _invoice.RevertTransfer(confirmed, _kit.Now)).Code);

        var t = _kit.Transfer("1");
        Record(_invoice, t, _kit.Now);
        Assert.NotEmpty(_invoice.RevertTransfer(t.Id, _kit.Now));
        Assert.Empty(_invoice.RevertTransfer(t.Id, _kit.Now));
    }

    [Fact]
    public void UnknownTransfer_Rejected()
    {
        var id = new Chain.TransferId(Hash(424242), 0);
        Assert.Equal(DomainErrorCodes.TransferNotFound, Assert.Throws<DomainException>(() => _invoice.ConfirmTransfer(id, _kit.Now)).Code);
        Assert.Equal(DomainErrorCodes.TransferNotFound, Assert.Throws<DomainException>(() => _invoice.RevertTransfer(id, _kit.Now)).Code);
    }

    [Fact]
    public void LateUnconfirmedTransfer_OnPaidInvoice_ConfirmsOrRevertsWithoutStatusChange()
    {
        Pay("10", blockTime: BeforeDeadline, confirm: false);
        AfterDeadline();
        var late = _kit.Transfer("1");
        Assert.Empty(Record(_invoice, late, _kit.Now)); // accepted while still confirming
        _invoice.ConfirmTransfer(_invoice.Transfers[0].Id, _kit.Now);
        Assert.Equal(InvoiceStatus.Paid, _invoice.Status);

        Assert.Empty(_invoice.ConfirmTransfer(late.Id, _kit.Now));
        Assert.Equal(InvoiceStatus.Paid, _invoice.Status);
        Assert.Equal(UsdcAmount.Parse("1"), _invoice.ExcessAmount); // visible to the merchant
    }

    [Fact]
    public void ReorgOnTerminalInvoice_EmitsOnlyPaymentReverted()
    {
        Pay("10", blockTime: BeforeDeadline, confirm: false);
        AfterDeadline();
        var late = _kit.Transfer("1");
        Record(_invoice, late, _kit.Now);
        _invoice.ConfirmTransfer(_invoice.Transfers[0].Id, _kit.Now);

        Assert.Equal([Reverted], Types(_invoice.RevertTransfer(late.Id, _kit.Now)));
        Assert.Equal(InvoiceStatus.Paid, _invoice.Status);
    }

    // ---- Events ---------------------------------------------------------------------------------

    [Fact]
    public void Events_HaveUniqueIds_AndCarryTransitionData()
    {
        var events = new List<InvoiceDomainEvent>();
        var t = _kit.Transfer("4");
        events.AddRange(_invoice.RecordTransfer(t, _kit.Now).Events);
        events.AddRange(_invoice.ConfirmTransfer(t.Id, _kit.Now));
        var t2 = _kit.Transfer("6");
        events.AddRange(_invoice.RecordTransfer(t2, _kit.Now).Events);
        events.AddRange(_invoice.RevertTransfer(t2.Id, _kit.Now));
        events.AddRange(_invoice.CheckDeadline(AfterDeadline()));

        Assert.Equal([Confirming, Underpaid, Confirming, Reverted, Underpaid, Expired], Types(events));
        Assert.Equal(events.Count, events.Select(e => e.EventId).Distinct().Count());
        Assert.All(events, e =>
        {
            Assert.Equal(_invoice.Id, e.InvoiceId);
            Assert.NotEqual(Guid.Empty, e.EventId);
        });

        var last = Assert.IsType<InvoiceStatusChanged>(events[^1]);
        Assert.Equal(InvoiceStatus.Underpaid, last.PreviousStatus);
        Assert.Equal(InvoiceStatus.Expired, last.Status);
        Assert.Equal(UsdcAmount.Parse("10"), last.Amount);
        Assert.Equal(UsdcAmount.Parse("4"), last.ConfirmedTotal);
        Assert.Equal(_kit.Now, last.OccurredAt);
        Assert.Equal(_kit.Now, _invoice.UpdatedAt);
    }

    [Theory]
    [InlineData(InvoiceStatus.Pending, "invoice.pending")]
    [InlineData(InvoiceStatus.Confirming, "invoice.confirming")]
    [InlineData(InvoiceStatus.Underpaid, "invoice.underpaid")]
    [InlineData(InvoiceStatus.Paid, "invoice.paid")]
    [InlineData(InvoiceStatus.Overpaid, "invoice.overpaid")]
    [InlineData(InvoiceStatus.Expired, "invoice.expired")]
    [InlineData(InvoiceStatus.PaidLate, "invoice.paid_late")]
    [InlineData(InvoiceStatus.Cancelled, "invoice.cancelled")]
    public void EventType_PerStatus(InvoiceStatus status, string type) => Assert.Equal(type, ForStatus(status));

    // ---- The transition table itself ------------------------------------------------------------

    public static TheoryData<InvoiceStatus, InvoiceStatus> DiagramEdges => new()
    {
        { InvoiceStatus.Pending, InvoiceStatus.Confirming },
        { InvoiceStatus.Pending, InvoiceStatus.Expired },
        { InvoiceStatus.Pending, InvoiceStatus.Cancelled },
        { InvoiceStatus.Confirming, InvoiceStatus.Paid },
        { InvoiceStatus.Confirming, InvoiceStatus.Overpaid },
        { InvoiceStatus.Confirming, InvoiceStatus.Underpaid },
        { InvoiceStatus.Confirming, InvoiceStatus.Pending },
        { InvoiceStatus.Underpaid, InvoiceStatus.Confirming },
        { InvoiceStatus.Underpaid, InvoiceStatus.Expired },
        { InvoiceStatus.Expired, InvoiceStatus.PaidLate },
        { InvoiceStatus.Expired, InvoiceStatus.Confirming },
    };

    [Fact]
    public void TransitionTable_IsExactlyTheDiagram()
    {
        var expected = DiagramEdges.Select(row => ((InvoiceStatus)row[0], (InvoiceStatus)row[1])).ToHashSet();
        foreach (var from in Enum.GetValues<InvoiceStatus>())
        {
            foreach (var to in Enum.GetValues<InvoiceStatus>())
            {
                Assert.Equal(expected.Contains((from, to)), Invoice.IsTransitionAllowed(from, to));
            }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private void DriveTo(InvoiceStatus status)
    {
        switch (status)
        {
            case InvoiceStatus.Pending:
                break;
            case InvoiceStatus.Confirming:
                Pay("10", confirm: false);
                break;
            case InvoiceStatus.Underpaid:
                Pay("4");
                break;
            case InvoiceStatus.Paid:
                Pay("10");
                break;
            case InvoiceStatus.Overpaid:
                Pay("11");
                break;
            case InvoiceStatus.Expired:
                _invoice.CheckDeadline(AfterDeadline());
                break;
            case InvoiceStatus.PaidLate:
                _invoice.CheckDeadline(AfterDeadline());
                Pay("10");
                break;
            case InvoiceStatus.Cancelled:
                _invoice.Cancel(_kit.Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
    }
}
