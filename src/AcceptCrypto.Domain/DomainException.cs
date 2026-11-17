namespace AcceptCrypto.Domain;

/// <summary>
/// A business rule was violated. <see cref="Code"/> is a stable machine-readable error code
/// (see <see cref="DomainErrorCodes"/>) that the API maps to an RFC 9457 Problem Details response.
/// </summary>
public class DomainException : Exception
{
    /// <summary>Creates an exception with an error code and a human-readable message.</summary>
    public DomainException(string code, string message)
        : base(message) => Code = code;

    /// <summary>Creates an exception without a specific code.</summary>
    public DomainException()
        : this(DomainErrorCodes.Unspecified, "A domain rule was violated.")
    {
    }

    /// <summary>Creates an exception without a specific code.</summary>
    public DomainException(string message)
        : this(DomainErrorCodes.Unspecified, message)
    {
    }

    /// <summary>Creates an exception without a specific code, wrapping another exception.</summary>
    public DomainException(string message, Exception innerException)
        : base(message, innerException) => Code = DomainErrorCodes.Unspecified;

    /// <summary>Stable machine-readable error code.</summary>
    public string Code { get; }
}

/// <summary>Stable error codes of <see cref="DomainException"/>.</summary>
public static class DomainErrorCodes
{
    /// <summary>No specific code.</summary>
    public const string Unspecified = "domain_error";

    /// <summary>Invoice amount outside the configured min/max.</summary>
    public const string AmountOutOfRange = "amount_out_of_range";

    /// <summary>Requested invoice lifetime outside the configured limits.</summary>
    public const string LifetimeOutOfRange = "lifetime_out_of_range";

    /// <summary>Missing or too long external order ID.</summary>
    public const string InvalidExternalOrderId = "invalid_external_order_id";

    /// <summary>Too many metadata entries, or a key/value that is empty or too long.</summary>
    public const string InvalidMetadata = "invalid_metadata";

    /// <summary>Idempotency key empty or too long.</summary>
    public const string InvalidIdempotencyKey = "invalid_idempotency_key";

    /// <summary>Malformed address, hash or derivation index.</summary>
    public const string InvalidChainData = "invalid_chain_data";

    /// <summary>The requested status change is not allowed from the current status.</summary>
    public const string InvalidTransition = "invalid_transition";

    /// <summary>The transfer was sent to another address than the invoice's deposit address.</summary>
    public const string TransferAddressMismatch = "transfer_address_mismatch";

    /// <summary>The transfer is not recorded on this invoice.</summary>
    public const string TransferNotFound = "transfer_not_found";

    /// <summary>The transfer is in a state that does not allow the operation (e.g. reverting a confirmed transfer).</summary>
    public const string TransferStateConflict = "transfer_state_conflict";
}
