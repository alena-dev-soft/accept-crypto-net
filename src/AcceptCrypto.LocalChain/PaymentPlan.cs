using System.Numerics;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.LocalChain;

/// <summary>Splits a payment into transfers (scenario S3: "pays in two parts").</summary>
public static class PaymentPlan
{
    /// <summary>
    /// Splits <paramref name="total"/> into <paramref name="parts"/> transfers that add up exactly to the total
    /// (no rounding loss, NFR-1). The base-unit remainder goes to the first parts, so parts differ by at most 1 unit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than 1 part, or more parts than base units.</exception>
    public static IReadOnlyList<UsdcAmount> Split(UsdcAmount total, int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        if (total.BaseUnits < parts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parts), $"Cannot split {total} USDC into {parts} non-zero transfers.");
        }

        var quotient = BigInteger.DivRem(total.BaseUnits, parts, out var remainder);
        var result = new UsdcAmount[parts];
        for (var i = 0; i < parts; i++)
        {
            result[i] = UsdcAmount.FromBaseUnits(quotient + (i < remainder ? BigInteger.One : BigInteger.Zero));
        }

        return result;
    }
}
