using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Invoices;

/// <summary>
/// Invoice aggregate: amount, deposit address, deadline, the transfers paid into it and its status.
/// </summary>
/// <remarks>
/// <para>
/// Status is never set directly by callers. Every operation changes the facts (transfers, deadline reached,
/// cancellation) and then <em>settles</em>: the target status is derived from the facts and the invoice walks
/// to it only along the edges of the state diagram (spec + docs/DECISIONS.md D5, D6, D11), emitting one
/// <see cref="InvoiceStatusChanged"/> per edge. So a single operation can produce several events, e.g.
/// <c>confirming → underpaid → expired</c> when the last on-time transfer confirms after the deadline.
/// </para>
/// <para>
/// Rules: tolerance 0; confirmed transfers add up; a transfer is on time if its block timestamp is before the
/// deadline; only confirmed transfers count; terminal invoices accept no new transfers.
/// </para>
/// </remarks>
public sealed class Invoice
{
    /// <summary>Largest non-hardened BIP32 child index.</summary>
    public const long MaxDerivationIndex = int.MaxValue;

    private static readonly Dictionary<InvoiceStatus, InvoiceStatus[]> AllowedTransitions = new()
    {
        [InvoiceStatus.Pending] = [InvoiceStatus.Confirming, InvoiceStatus.Expired, InvoiceStatus.Cancelled],
        [InvoiceStatus.Confirming] = [InvoiceStatus.Paid, InvoiceStatus.Overpaid, InvoiceStatus.Underpaid, InvoiceStatus.Pending],
        [InvoiceStatus.Underpaid] = [InvoiceStatus.Confirming, InvoiceStatus.Expired],
        [InvoiceStatus.Expired] = [InvoiceStatus.PaidLate, InvoiceStatus.Confirming],
        [InvoiceStatus.Paid] = [],
        [InvoiceStatus.Overpaid] = [],
        [InvoiceStatus.PaidLate] = [],
        [InvoiceStatus.Cancelled] = [],
    };

    private readonly List<ObservedTransfer> _transfers = [];

#pragma warning disable CS8618 // Persistence constructor: properties are set by the ORM.
    private Invoice()
#pragma warning restore CS8618
    {
    }

    /// <summary>Invoice ID (UUIDv7).</summary>
    public Guid Id { get; private set; }

    /// <summary>Amount due.</summary>
    public UsdcAmount Amount { get; private set; }

    /// <summary>The merchant's order ID.</summary>
    public string ExternalOrderId { get; private set; }

    /// <summary>Merchant metadata (never shown to the payer).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; private set; }

    /// <summary>Idempotency key of the create request, if any (FR-2; uniqueness is enforced by storage).</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Deposit address (lower case), unique per invoice (D1).</summary>
    public string DepositAddress { get; private set; }

    /// <summary>BIP32 child index the deposit address was derived at.</summary>
    public long DerivationIndex { get; private set; }

    /// <summary>Unguessable token of the public payment page (FR-29).</summary>
    public string PublicToken { get; private set; }

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Deadline: payments in blocks with a timestamp before it are on time (FR-3).</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the invoice first became <see cref="InvoiceStatus.Expired"/>; once set, the deadline counts as passed for good.</summary>
    public DateTimeOffset? ExpiredAt { get; private set; }

    /// <summary>Current status.</summary>
    public InvoiceStatus Status { get; private set; }

    /// <summary>Time of the last status change.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>All transfers ever recorded, including reverted ones (FR-19).</summary>
    public IReadOnlyList<ObservedTransfer> Transfers => _transfers;

    /// <summary>Sum of confirmed transfers, on time and late.</summary>
    public UsdcAmount ConfirmedTotal => Sum(t => t.State == TransferState.Confirmed);

    /// <summary>Sum of confirmed transfers made before the deadline.</summary>
    public UsdcAmount ConfirmedOnTimeTotal => Sum(t => t.State == TransferState.Confirmed && t.IsOnTime);

    /// <summary>Sum of seen transfers still waiting for confirmations.</summary>
    public UsdcAmount UnconfirmedTotal => Sum(t => t.State == TransferState.Seen);

    /// <summary>What is still missing to the amount, based on confirmed transfers (zero if fully paid).</summary>
    public UsdcAmount RemainingAmount => Amount.SubtractOrZero(ConfirmedTotal);

    /// <summary>Confirmed amount above the invoice amount (zero if none).</summary>
    public UsdcAmount ExcessAmount => ConfirmedTotal.SubtractOrZero(Amount);

    /// <summary>Creates a pending invoice after validating the input against the policy.</summary>
    /// <exception cref="DomainException">Invalid input (see <see cref="DomainErrorCodes"/>).</exception>
    public static Invoice Create(NewInvoice request, InvoicePolicy policy, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);

        if (request.Amount < policy.MinAmount || request.Amount > policy.MaxAmount)
        {
            throw new DomainException(
                DomainErrorCodes.AmountOutOfRange,
                $"Amount must be between {policy.MinAmount} and {policy.MaxAmount} USDC.");
        }

        var lifetime = request.Lifetime ?? policy.DefaultLifetime;
        if (lifetime < policy.MinLifetime || lifetime > policy.MaxLifetime)
        {
            throw new DomainException(
                DomainErrorCodes.LifetimeOutOfRange,
                $"Lifetime must be between {policy.MinLifetime} and {policy.MaxLifetime}.");
        }

        if (string.IsNullOrWhiteSpace(request.ExternalOrderId) || request.ExternalOrderId.Length > policy.MaxExternalOrderIdLength)
        {
            throw new DomainException(
                DomainErrorCodes.InvalidExternalOrderId,
                $"External order ID is required and at most {policy.MaxExternalOrderIdLength} characters.");
        }

        if (request.IdempotencyKey is not null
            && (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > policy.MaxIdempotencyKeyLength))
        {
            throw new DomainException(
                DomainErrorCodes.InvalidIdempotencyKey,
                $"Idempotency key must be non-empty and at most {policy.MaxIdempotencyKeyLength} characters.");
        }

        if (request.DerivationIndex is < 0 or > MaxDerivationIndex)
        {
            throw new DomainException(DomainErrorCodes.InvalidChainData, "Derivation index must be a non-hardened BIP32 index (0 .. 2^31-1).");
        }

        var now = clock.GetUtcNow();
        return new Invoice
        {
            Id = Guid.CreateVersion7(now),
            Amount = request.Amount,
            ExternalOrderId = request.ExternalOrderId,
            Metadata = ValidateMetadata(request.Metadata, policy),
            IdempotencyKey = request.IdempotencyKey,
            DepositAddress = EvmHex.NormalizeAddress(request.DepositAddress, nameof(request.DepositAddress)),
            DerivationIndex = request.DerivationIndex,
            PublicToken = PaymentPageToken.Generate(),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            Status = InvoiceStatus.Pending,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Records a transfer seen on-chain (not yet confirmed). Idempotent: a transfer already recorded is reported
    /// as <see cref="TransferRecordOutcome.Duplicate"/>; a reverted one that re-appears (re-included after a reorg)
    /// is reinstated. A terminal invoice accepts nothing new (<see cref="TransferRecordOutcome.InvoiceClosed"/>).
    /// </summary>
    /// <exception cref="DomainException">The transfer is not addressed to this invoice's deposit address.</exception>
    public TransferRecordResult RecordTransfer(ChainTransfer transfer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (!string.Equals(transfer.To, DepositAddress, StringComparison.Ordinal))
        {
            throw new DomainException(
                DomainErrorCodes.TransferAddressMismatch,
                $"Transfer {transfer.Id} is sent to {transfer.To}, not to the invoice address {DepositAddress}.");
        }

        var existing = FindTransfer(transfer.Id);
        if (existing is not null && existing.State != TransferState.Reverted)
        {
            return new TransferRecordResult(TransferRecordOutcome.Duplicate, []);
        }

        if (Status.IsTerminal())
        {
            return new TransferRecordResult(TransferRecordOutcome.InvoiceClosed, []);
        }

        var isOnTime = transfer.BlockTimestamp < ExpiresAt;
        if (existing is null)
        {
            _transfers.Add(new ObservedTransfer(transfer, isOnTime, now));
        }
        else
        {
            existing.Reinstate(transfer, isOnTime);
        }

        var events = new List<InvoiceDomainEvent>();
        Settle(now, events);
        return new TransferRecordResult(TransferRecordOutcome.Accepted, events);
    }

    /// <summary>Marks a seen transfer as confirmed (N confirmations reached, FR-7). Idempotent.</summary>
    /// <exception cref="DomainException">Unknown or reverted transfer.</exception>
    public IReadOnlyList<InvoiceDomainEvent> ConfirmTransfer(TransferId transferId, DateTimeOffset now)
    {
        var transfer = GetTransfer(transferId);
        switch (transfer.State)
        {
            case TransferState.Confirmed:
                return [];
            case TransferState.Reverted:
                throw new DomainException(DomainErrorCodes.TransferStateConflict, $"Transfer {transferId} was reverted and cannot be confirmed.");
        }

        transfer.Confirm(now);
        var events = new List<InvoiceDomainEvent>();
        Settle(now, events);
        return events;
    }

    /// <summary>
    /// A seen transfer disappeared from the canonical chain (reorg, FR-10): emits <c>payment.reverted</c> and
    /// moves the invoice back as needed. Idempotent for an already reverted transfer.
    /// </summary>
    /// <exception cref="DomainException">Unknown transfer, or the transfer is already confirmed (final).</exception>
    public IReadOnlyList<InvoiceDomainEvent> RevertTransfer(TransferId transferId, DateTimeOffset now)
    {
        var transfer = GetTransfer(transferId);
        switch (transfer.State)
        {
            case TransferState.Reverted:
                return [];
            case TransferState.Confirmed:
                throw new DomainException(DomainErrorCodes.TransferStateConflict, $"Transfer {transferId} is confirmed and final; it cannot be reverted.");
        }

        var orphanedBlockHash = transfer.BlockHash;
        transfer.Revert(now);
        var events = new List<InvoiceDomainEvent>
        {
            new PaymentReverted(Guid.CreateVersion7(now), Id, now, transfer.Id, transfer.Amount, transfer.BlockNumber, orphanedBlockHash),
        };
        Settle(now, events);
        return events;
    }

    /// <summary>
    /// Re-evaluates the invoice at <paramref name="now"/>; moves it to <see cref="InvoiceStatus.Expired"/> if the
    /// deadline has passed and nothing on time is still confirming. Called by the expiry worker. Idempotent.
    /// </summary>
    public IReadOnlyList<InvoiceDomainEvent> CheckDeadline(DateTimeOffset now)
    {
        var events = new List<InvoiceDomainEvent>();
        Settle(now, events);
        return events;
    }

    /// <summary>Cancels the invoice. Only allowed while <see cref="InvoiceStatus.Pending"/> (D6).</summary>
    /// <exception cref="DomainException">The invoice is not pending (<see cref="DomainErrorCodes.InvalidTransition"/>).</exception>
    public IReadOnlyList<InvoiceDomainEvent> Cancel(DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Pending)
        {
            throw new DomainException(DomainErrorCodes.InvalidTransition, $"Only a pending invoice can be cancelled; this one is {Status}.");
        }

        var events = new List<InvoiceDomainEvent>();
        TransitionTo(InvoiceStatus.Cancelled, now, events);
        return events;
    }

    /// <summary><c>true</c> if the state diagram has a direct edge <paramref name="from"/> → <paramref name="to"/>.</summary>
    public static bool IsTransitionAllowed(InvoiceStatus from, InvoiceStatus to) =>
        AllowedTransitions.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    private bool IsDeadlinePassed(DateTimeOffset now) =>
        now >= ExpiresAt
        || ExpiredAt is not null
        || _transfers.Exists(t => t.State != TransferState.Reverted && !t.IsOnTime); // chain time says so

    /// <summary>Status the facts call for, ignoring how to get there.</summary>
    private InvoiceStatus ComputeTargetStatus(DateTimeOffset now)
    {
        if (_transfers.Exists(t => t.State == TransferState.Seen && t.IsOnTime))
        {
            return InvoiceStatus.Confirming;
        }

        var onTime = ConfirmedOnTimeTotal;
        if (onTime > Amount)
        {
            return InvoiceStatus.Overpaid;
        }

        if (onTime == Amount)
        {
            return InvoiceStatus.Paid;
        }

        if (!IsDeadlinePassed(now))
        {
            return onTime.IsZero ? InvoiceStatus.Pending : InvoiceStatus.Underpaid;
        }

        // Late transfers count only after the deadline, and only to reach paid_late (D11: also when they overpay).
        return ConfirmedTotal >= Amount ? InvoiceStatus.PaidLate : InvoiceStatus.Expired;
    }

    /// <summary>Next edge of the diagram on the way from <paramref name="from"/> to <paramref name="target"/>.</summary>
    private InvoiceStatus NextStep(InvoiceStatus from, InvoiceStatus target)
    {
        if (IsTransitionAllowed(from, target))
        {
            return target;
        }

        return (from, target) switch
        {
            // confirming has no edge to expired: go back to pending/underpaid first, then expire.
            (InvoiceStatus.Confirming, InvoiceStatus.Expired or InvoiceStatus.PaidLate) =>
                ConfirmedOnTimeTotal.IsZero ? InvoiceStatus.Pending : InvoiceStatus.Underpaid,
            (InvoiceStatus.Pending or InvoiceStatus.Underpaid, InvoiceStatus.PaidLate) => InvoiceStatus.Expired,
            _ => throw new InvalidOperationException($"Invoice {Id}: no path from {from} to {target}."),
        };
    }

    private void Settle(DateTimeOffset now, List<InvoiceDomainEvent> events)
    {
        // The longest path (confirming → pending → expired → paid_late) has 3 edges.
        for (var guard = 0; guard < 8 && !Status.IsTerminal(); guard++)
        {
            var target = ComputeTargetStatus(now);
            if (target == Status)
            {
                return;
            }

            TransitionTo(NextStep(Status, target), now, events);
        }

        if (!Status.IsTerminal() && ComputeTargetStatus(now) != Status)
        {
            throw new InvalidOperationException($"Invoice {Id}: status did not settle.");
        }
    }

    private void TransitionTo(InvoiceStatus next, DateTimeOffset now, List<InvoiceDomainEvent> events)
    {
        if (!IsTransitionAllowed(Status, next))
        {
            throw new InvalidOperationException($"Invoice {Id}: transition {Status} → {next} is not in the state diagram.");
        }

        var previous = Status;
        Status = next;
        UpdatedAt = now;
        if (next == InvoiceStatus.Expired)
        {
            ExpiredAt ??= now;
        }

        events.Add(new InvoiceStatusChanged(Guid.CreateVersion7(now), Id, now, previous, next, Amount, ConfirmedTotal));
    }

    private ObservedTransfer? FindTransfer(TransferId id) => _transfers.Find(t => t.Id == id);

    private ObservedTransfer GetTransfer(TransferId id) =>
        FindTransfer(id) ?? throw new DomainException(DomainErrorCodes.TransferNotFound, $"Transfer {id} is not recorded on invoice {Id}.");

    private UsdcAmount Sum(Predicate<ObservedTransfer> filter)
    {
        var total = UsdcAmount.Zero;
        foreach (var t in _transfers)
        {
            if (filter(t))
            {
                total += t.Amount;
            }
        }

        return total;
    }

    private static Dictionary<string, string> ValidateMetadata(IReadOnlyDictionary<string, string>? metadata, InvoicePolicy policy)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        if (metadata.Count > policy.MaxMetadataEntries)
        {
            throw new DomainException(DomainErrorCodes.InvalidMetadata, $"At most {policy.MaxMetadataEntries} metadata entries are allowed.");
        }

        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > policy.MaxMetadataKeyLength
                || value is null || value.Length > policy.MaxMetadataValueLength)
            {
                throw new DomainException(
                    DomainErrorCodes.InvalidMetadata,
                    $"Metadata keys must be 1–{policy.MaxMetadataKeyLength} characters and values at most {policy.MaxMetadataValueLength}.");
            }

            result[key] = value;
        }

        return result;
    }
}
