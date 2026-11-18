using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcceptCrypto.Sdk;

/// <summary>
/// An exact USDC amount (6 decimals), stored as integer base units — no floating point, no precision loss (NFR-1).
/// On the wire it is a decimal string (<c>"25.5"</c>).
/// </summary>
[JsonConverter(typeof(UsdcAmountJsonConverter))]
public readonly struct UsdcAmount : IEquatable<UsdcAmount>, IComparable<UsdcAmount>
{
    /// <summary>Decimals of the token (USDC: 6).</summary>
    public const int Decimals = 6;

    private static readonly BigInteger UnitsPerToken = BigInteger.Pow(10, Decimals);

    private UsdcAmount(BigInteger baseUnits) => BaseUnits = baseUnits;

    /// <summary>Creates an amount from a <see cref="decimal"/>, e.g. <c>new UsdcAmount(25.50m)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Negative, or more than 6 decimals.</exception>
    public UsdcAmount(decimal value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A USDC amount cannot be negative.");
        }

        var scaled = value * 1_000_000m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"A USDC amount has at most {Decimals} decimals.");
        }

        BaseUnits = new BigInteger(scaled);
    }

    /// <summary>Zero.</summary>
    public static UsdcAmount Zero => default;

    /// <summary>The amount in base units (1 USDC = 1 000 000).</summary>
    public BigInteger BaseUnits { get; }

    /// <summary>Creates an amount from base units (1 USDC = 1 000 000).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Negative.</exception>
    public static UsdcAmount FromBaseUnits(BigInteger baseUnits) =>
        baseUnits.Sign < 0
            ? throw new ArgumentOutOfRangeException(nameof(baseUnits), "A USDC amount cannot be negative.")
            : new UsdcAmount(baseUnits);

    /// <summary>
    /// Parses a decimal string with invariant culture: digits, optionally a <c>.</c> and 1–6 digits
    /// (<c>"25"</c>, <c>"25.50"</c>). No sign, exponent, whitespace or group separators.
    /// </summary>
    /// <exception cref="FormatException">Not a valid amount.</exception>
    public static UsdcAmount Parse(string value) =>
        TryParse(value, out var amount)
            ? amount
            : throw new FormatException($"'{value}' is not a valid USDC amount: expected a decimal string with at most {Decimals} decimals, e.g. \"25.50\".");

    /// <summary>Tries to parse a decimal string; see <see cref="Parse"/>.</summary>
    public static bool TryParse(string? value, out UsdcAmount amount)
    {
        amount = Zero;
        if (string.IsNullOrEmpty(value) || value.Length > 100)
        {
            return false;
        }

        var dot = value.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? value : value[..dot];
        var fraction = dot < 0 ? string.Empty : value[(dot + 1)..];
        if (whole.Length == 0 || !IsDigits(whole) || (dot >= 0 && (fraction.Length is 0 or > Decimals || !IsDigits(fraction))))
        {
            return false;
        }

        var units = BigInteger.Parse(whole, NumberStyles.None, CultureInfo.InvariantCulture) * UnitsPerToken;
        if (fraction.Length > 0)
        {
            units += BigInteger.Parse(fraction.PadRight(Decimals, '0'), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        amount = new UsdcAmount(units);
        return true;
    }

    /// <summary>Converts to <see cref="decimal"/> (exact for every amount below ~7.9·10²²).</summary>
    /// <exception cref="OverflowException">Too large for a decimal.</exception>
    public decimal ToDecimal() => (decimal)BaseUnits / 1_000_000m;

    /// <summary>Canonical form, as the service sends it: <c>"25.5"</c>, <c>"10"</c>.</summary>
    public override string ToString()
    {
        var whole = BigInteger.DivRem(BaseUnits, UnitsPerToken, out var fraction);
        var wholeText = whole.ToString(CultureInfo.InvariantCulture);
        if (fraction.IsZero)
        {
            return wholeText;
        }

        return wholeText + "." + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(Decimals, '0').TrimEnd('0');
    }

    /// <inheritdoc />
    public bool Equals(UsdcAmount other) => BaseUnits.Equals(other.BaseUnits);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UsdcAmount other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => BaseUnits.GetHashCode();

    /// <inheritdoc />
    public int CompareTo(UsdcAmount other) => BaseUnits.CompareTo(other.BaseUnits);

    /// <summary>Equality.</summary>
    public static bool operator ==(UsdcAmount left, UsdcAmount right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(UsdcAmount left, UsdcAmount right) => !left.Equals(right);

    /// <summary>Less than.</summary>
    public static bool operator <(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) < 0;

    /// <summary>Greater than.</summary>
    public static bool operator >(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) > 0;

    /// <summary>Less than or equal.</summary>
    public static bool operator <=(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) <= 0;

    /// <summary>Greater than or equal.</summary>
    public static bool operator >=(UsdcAmount left, UsdcAmount right) => left.CompareTo(right) >= 0;

    private static bool IsDigits(string s)
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

/// <summary>Reads and writes <see cref="UsdcAmount"/> as a JSON decimal string; JSON numbers are refused.</summary>
internal sealed class UsdcAmountJsonConverter : JsonConverter<UsdcAmount>
{
    public override UsdcAmount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A USDC amount must be a JSON string.");
        }

        var text = reader.GetString();
        return UsdcAmount.TryParse(text, out var amount) ? amount : throw new JsonException($"'{text}' is not a valid USDC amount.");
    }

    public override void Write(Utf8JsonWriter writer, UsdcAmount value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
