namespace AcceptCrypto.Sdk;

/// <summary>
/// Typed client for the AcceptCrypto.NET merchant API (FR-24). Errors surface as <see cref="AcceptCryptoApiException"/>
/// subclasses (service answered with Problem Details) or <see cref="AcceptCryptoConnectionException"/> (FR-25).
/// Register with <c>services.AddAcceptCrypto(…)</c> and inject this interface.
/// </summary>
public interface IAcceptCryptoClient
{
    /// <summary>Creates an invoice (FR-1). A repeated call with the same <paramref name="idempotencyKey"/> returns the original invoice (FR-2).</summary>
    /// <param name="request">Amount, order ID, metadata, lifetime.</param>
    /// <param name="idempotencyKey">1–255 printable ASCII characters, e.g. your order ID. Null: a random key (see <see cref="AcceptCryptoOptions.GenerateIdempotencyKeys"/>).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="AcceptCryptoValidationException">Invalid request.</exception>
    /// <exception cref="AcceptCryptoIdempotencyKeyReusedException">The key was used for a different request.</exception>
    Task<CreatedInvoice> CreateInvoiceAsync(CreateInvoiceRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default);

    /// <summary>Gets an invoice with its transfers and webhook history (FR-19).</summary>
    /// <exception cref="AcceptCryptoNotFoundException">No such invoice.</exception>
    Task<InvoiceDetails> GetInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Lists invoices, newest first, one page (FR-18).</summary>
    Task<Page<Invoice>> ListInvoicesAsync(ListInvoicesQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Enumerates all invoices matching the filter, following the cursor page by page.</summary>
    IAsyncEnumerable<Invoice> ListAllInvoicesAsync(ListInvoicesQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending invoice (idempotent: cancelling a cancelled invoice succeeds).</summary>
    /// <exception cref="AcceptCryptoConflictException">The invoice is no longer pending (see <see cref="AcceptCryptoConflictException.CurrentStatus"/>).</exception>
    /// <exception cref="AcceptCryptoNotFoundException">No such invoice.</exception>
    Task<Invoice> CancelInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Lists transfers that matched no open invoice, one page (FR-11a).</summary>
    Task<Page<UnmatchedPayment>> ListUnmatchedPaymentsAsync(ListUnmatchedPaymentsQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Enumerates all unmatched payments, following the cursor page by page.</summary>
    IAsyncEnumerable<UnmatchedPayment> ListAllUnmatchedPaymentsAsync(ListUnmatchedPaymentsQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Queues a webhook event again; it keeps its event ID and payload (FR-15).</summary>
    /// <exception cref="AcceptCryptoNotFoundException">No such event.</exception>
    Task<WebhookEventInfo> ResendEventAsync(Guid eventId, CancellationToken cancellationToken = default);
}
