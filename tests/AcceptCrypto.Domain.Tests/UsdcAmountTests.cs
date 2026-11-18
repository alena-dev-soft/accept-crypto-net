using System.Numerics;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Tests;

public class UsdcAmountTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1_000_000L)]
    [InlineData("10.5", 10_500_000L)]
    [InlineData("0.000001", 1L)]
    [InlineData("0.1", 100_000L)]
    [InlineData("123456.789012", 123_456_789_012L)]
    [InlineData("1.000000", 1_000_000L)]
    [InlineData("9223372036854.775807", long.MaxValue)]
    public void Parse_ValidStrings_ExactBaseUnits(string text, long expectedUnits) =>
        Assert.Equal(new BigInteger(expectedUnits), UsdcAmount.Parse(text).BaseUnits);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1.0000001")]
    [InlineData("0.0000001")]
    [InlineData("01")]
    [InlineData("00.5")]
    [InlineData("1,5")]
    [InlineData("1,000")]
    [InlineData("1e6")]
    [InlineData("1.2.3")]
    [InlineData("NaN")]
    [InlineData("0x10")]
    [InlineData("١")] // non-ASCII digit
    public void Parse_InvalidStrings_Rejected(string text)
    {
        Assert.False(UsdcAmount.TryParse(text, out _));
        Assert.Throws<FormatException>(() => UsdcAmount.Parse(text));
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse() => Assert.False(UsdcAmount.TryParse(null, out _));

    [Fact]
    public void Parse_HugeValues_NoPrecisionLoss()
    {
        // Larger than decimal and long; still exact.
        var text = "123456789012345678901234567890.123456";
        var amount = UsdcAmount.Parse(text);
        Assert.Equal(BigInteger.Parse("123456789012345678901234567890123456", System.Globalization.CultureInfo.InvariantCulture), amount.BaseUnits);
        Assert.Equal(text, amount.ToString());
    }

    [Fact]
    public void Parse_AboveUint256_Rejected()
    {
        var max = UsdcAmount.FromBaseUnits(UsdcAmount.MaxBaseUnits);
        Assert.Equal(max, UsdcAmount.Parse(max.ToString()));
        Assert.False(UsdcAmount.TryParse((max + UsdcAmount.FromBaseUnits(1)).ToString(), out _));
        Assert.False(UsdcAmount.TryParse(new string('9', 100), out _));
    }

    [Theory]
    [InlineData("10.500000", "10.5", "10.500000")]
    [InlineData("10", "10", "10.000000")]
    [InlineData("100", "100", "100.000000")]
    [InlineData("0", "0", "0.000000")]
    [InlineData("0.000001", "0.000001", "0.000001")]
    [InlineData("7.12", "7.12", "7.120000")]
    public void Format_CanonicalAndFixed(string input, string canonical, string fixedForm)
    {
        var amount = UsdcAmount.Parse(input);
        Assert.Equal(canonical, amount.ToString());
        Assert.Equal(fixedForm, amount.ToFixedString());
        Assert.Equal(amount, UsdcAmount.Parse(amount.ToString())); // round trip
    }

    [Fact]
    public void FromBaseUnits_RejectsNegativeAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UsdcAmount.FromBaseUnits(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => UsdcAmount.FromBaseUnits(UsdcAmount.MaxBaseUnits + 1));
        Assert.Equal("0.000123", UsdcAmount.FromBaseUnits(123).ToString());
    }

    [Fact]
    public void Arithmetic_IsExact()
    {
        // 0.1 + 0.2 == 0.3, unlike binary floating point.
        Assert.Equal(UsdcAmount.Parse("0.3"), UsdcAmount.Parse("0.1") + UsdcAmount.Parse("0.2"));
        Assert.Equal(UsdcAmount.Parse("2.5"), UsdcAmount.Parse("10") - UsdcAmount.Parse("7.5"));
        Assert.Throws<InvalidOperationException>(() => UsdcAmount.Parse("1") - UsdcAmount.Parse("1.000001"));
        Assert.Equal(UsdcAmount.Zero, UsdcAmount.Parse("1").SubtractOrZero(UsdcAmount.Parse("2")));
        Assert.Equal(UsdcAmount.Parse("1"), UsdcAmount.Parse("2").SubtractOrZero(UsdcAmount.Parse("1")));
    }

    [Fact]
    public void Comparison_And_Equality()
    {
        var a = UsdcAmount.Parse("1.5");
        var b = UsdcAmount.Parse("1.500000");
        var c = UsdcAmount.Parse("1.500001");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a < c && c > a && a <= b && a >= b);
        Assert.False(a > c);
        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a.CompareTo((object?)null) > 0);
        Assert.Throws<ArgumentException>(() => a.CompareTo("1.5"));
        Assert.True(UsdcAmount.Zero.IsZero);
        Assert.Equal(UsdcAmount.Zero, default);
    }
}
