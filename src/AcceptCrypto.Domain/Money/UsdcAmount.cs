using System.Globalization;
using System.Numerics;
using System.Text;

namespace AcceptCrypto.Domain.Money;

/// <summary>
/// A non-negative USDC amount stored as an integer number of base units (1 USDC = 10^6 base units).
/// No floating point is involved anywhere, so parsing, arithmetic and comparison are exact (NFR-1).
/// </summary>
/// <remarks>
/// The canonical text form is a plain decimal string with at most <see cref="Decimals"/> fractional digits,
/// e.g. <c>"10"</c>, <c>"10.5"</c>, <c>"0.000001"</c> (FR-1). Base units are bounded by the ERC-20
/// <c>uint256</c> range when parsed or created from chain data.
/// </remarks>
public readonly record struct UsdcAmount : IComparable<UsdcAmount>, IComparable
{
    /// <summary>Number of decimals of the token (USDC: 6).</summary>
    public const int Decimals = 6;

    private static readonly BigInteger UnitsPerToken = BigInteger.Pow(10, Decimals);

    /// <summary>Largest representable value: <c>2^256 - 1</c> base units (ERC-20 <c>uint256</c>).</summary>
    public static readonly BigInteger MaxBaseUnits = (BigInteger.One << 256) - BigInteger.One;

    private UsdcAmount(BigInteger baseUnits) => BaseUnits = baseUnits;

    /// <summary>The amount in base units (millionths of a USDC).</summary>
    public BigInteger BaseUnits { get; }

    /// <summary>Zero USDC.</summary>
    public static UsdcAmount Zero => default;

    /// <summary><c>true</c> when the amount is zero.</summary>
    public bool IsZero => BaseUnits.IsZero;

    /// <summary>Creates an amount from base units (e.g. the <c>value</c> of an ERC-20 <c>Transfer</c> log).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Negative or above <see cref="MaxBaseUnits"/>.</exception>
    public static UsdcAmount FromBaseUnits(BigInteger baseUnits)
    {
        if (baseUnits.Sign < 0 || baseUnits > MaxBaseUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(baseUnits), baseUnits, "Base units must be within 0 .. 2^256-1.");
        }

        return new UsdcAmount(baseUnits);
    }

    /// <summary>Creates an amount from base units.</summary>
    public static UsdcAmount FromBaseUnits(long baseUnits) => FromBaseUnits(new BigInteger(baseUnits));

    /// <summary>
    /// Parses a decimal string: ASCII digits, no sign, no leading zeros (except a single <c>0</c>),
    /// optionally a <c>.</c> followed by 1–6 digits. No whitespace, exponent, group separators or culture rules.
    /// </summary>
    /// <exception cref="FormatException">The string is not a valid amount.</exception>
    public static UsdcAmount Parse(string value)
    {
        if (!TryParse(value, out var amount))
        {
            throw new FormatException(
                $"'{value}' is not a valid USDC amount: expected a non-negative decimal string with at most {Decimals} decimals, e.g. \"12.5\".");
        }

        return amount;
    }

    /// <summary>Tries to parse a decimal string; see <see cref="Parse"/> for the accepted format.</summary>
    public static bool TryParse(string? value, out UsdcAmount amount)
    {
        amount = Zero;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var dot = value.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot < 0 ? value : value[..dot];
        var fractionPart = dot < 0 ? string.Empty : value[(dot + 1)..];

        if (integerPart.Length == 0 || !IsAsciiDigits(integerPart) || (integerPart.Length > 1 && integerPart[0] == '0'))
        {
            return false;
        }

        if (dot >= 0 && (fractionPart.Length is 0 or > Decimals || !IsAsciiDigits(fractionPart)))
        {
            return false;
        }

        // 2^256 has 78 digits; anything longer is out of range anyway and keeps BigInteger parsing cheap.
        if (integerPart.Length > 78)
        {
            return false;
        }

        var units = BigInteger.Parse(integerPart, NumberStyles.None, CultureInfo.InvariantCulture) * UnitsPerToken;
        if (fractionPart.Length > 0)
        {
            units += BigInteger.Parse(fractionPart.PadRight(Decimals, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (units > MaxBaseUnits)
        {
            return false;
        }

        amount = new UsdcAmount(units);
        return true;
    }

    /// <summary>Canonical form: no trailing fractional zeros, no decimal point for whole amounts (<c>"10.5"</c>, <c>"3"</c>).</summary>
    public override string ToString()
    {
        var fixedForm = ToFixedString();
        return fixedForm.TrimEnd('0').TrimEnd('.');
    }

    /// <summary>Fixed form with exactly 6 decimals, e.g. <c>"10.500000"</c>.</summary>
    public string ToFixedString()
    {
        var whole = BigInteger.DivRem(BaseUnits, UnitsPerToken, out var fraction);
        return new StringBuilder()
            .Append(whole.ToString(CultureInfo.InvariantCulture))
            .Append('.')
            .Append(fraction.ToString(CultureInfo.InvariantCulture).PadLeft(Decimals, '0'))
            .ToString();
    }

    /// <summary>Sum of two amounts.</summary>
    public UsdcAmount Add(UsdcAmount other) => new(BaseUnits + other.BaseUnits);

    /// <summary>Difference of two amounts.</summary>
    /// <exception cref="InvalidOperationException">The result would be negative.</exception>
    public UsdcAmount Subtract(UsdcAmount other)
    {
        if (other.BaseUnits > BaseUnits)
        {
            throw new InvalidOperationException($"Cannot subtract {other} from {this}: amounts are non-negative.");
        }

        return new UsdcAmount(BaseUnits - other.BaseUnits);
    }

    /// <summary>Difference, floored at zero (e.g. the amount still due).</summary>
    public UsdcAmount SubtractOrZero(UsdcAmount other) => other.BaseUnits >= BaseUnits ? Zero : new UsdcAmount(BaseUnits - other.BaseUnits);

    /// <inheritdoc />
    public int CompareTo(UsdcAmount other) => BaseUnits.CompareTo(other.BaseUnits);

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        UsdcAmount other => CompareTo(other),
        _ => throw new ArgumentException($"Object must be of type {nameof(UsdcAmount)}.", nameof(obj)),
    };

    /// <summary>Sum of two amounts.</summary>
    public static UsdcAmount operator +(UsdcAmount left, UsdcAmount right) => left.Add(right);

    /// <summary>Difference of two amounts; throws if negative.</summary>
    public static UsdcAmount operator -(UsdcAmount left, UsdcAmount right) => left.Subtract(right);

    /// <summary>Less than.</summary>
    public static bool operator <(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) < 0;

    /// <summary>Greater than.</summary>
    public static bool operator >(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) > 0;

    /// <summary>Less than or equal.</summary>
    public static bool operator <=(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) <= 0;

    /// <summary>Greater than or equal.</summary>
    public static bool operator >=(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) >= 0;

    private static bool IsAsciiDigits(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
