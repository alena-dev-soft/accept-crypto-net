using System.Text.Json;
using AcceptCrypto.Sdk.Json;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.Extensions.Time.Testing;

namespace AcceptCrypto.Sdk.Tests;

public class UsdcAmountTests
{
    [Theory]
    [InlineData("0", 0, "0")]
    [InlineData("25.50", 25_500_000, "25.5")]
    [InlineData("0.000001", 1, "0.000001")]
    [InlineData("10.000", 10_000_000, "10")]
    [InlineData("007", 7_000_000, "7")]
    public void ParsesAndFormatsCanonically(string text, long baseUnits, string canonical)
    {
        var amount = UsdcAmount.Parse(text);

        Assert.Equal(baseUnits, (long)amount.BaseUnits);
        Assert.Equal(canonical, amount.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1.0000001")]
    [InlineData("-1")]
    [InlineData("1e3")]
    [InlineData("1,5")]
    [InlineData(" 1")]
    public void RejectsInvalidStrings(string text) => Assert.False(UsdcAmount.TryParse(text, out _));

    [Fact]
    public void DecimalConversionIsExactAndRefusesExtraPrecision()
    {
        Assert.Equal(UsdcAmount.Parse("0.1"), new UsdcAmount(0.1m));
        Assert.Equal(UsdcAmount.Parse("0.3").BaseUnits, new UsdcAmount(0.1m).BaseUnits + new UsdcAmount(0.2m).BaseUnits);
        Assert.Equal(25.5m, UsdcAmount.Parse("25.5").ToDecimal());
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsdcAmount(0.0000001m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsdcAmount(-1m));
    }

    [Fact]
    public void ComparesByValue()
    {
        Assert.True(UsdcAmount.Parse("10") > UsdcAmount.Parse("9.999999"));
        Assert.Equal(UsdcAmount.Parse("10.0"), UsdcAmount.Parse("10"));
    }

    [Fact]
    public void SerializesAsAJsonString()
    {
        Assert.Equal("\"25.5\"", JsonSerializer.Serialize(new UsdcAmount(25.50m), AcceptCryptoJson.Options));
        Assert.Equal(new UsdcAmount(1.5m), JsonSerializer.Deserialize<UsdcAmount>("\"1.5\"", AcceptCryptoJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UsdcAmount>("1.5", AcceptCryptoJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UsdcAmount>("\"1.1234567\"", AcceptCryptoJson.Options));
    }
}

public class DeduplicationTests
{
    private static readonly AcceptCryptoEvent Event = AcceptCryptoEvent.Parse(TestData.PaidEventJson);

    [Fact]
    public async Task ACompletedEventIsADuplicate()
    {
        var dedup = new InMemoryWebhookEventDeduplicator();

        Assert.Equal(WebhookEventClaim.New, await dedup.TryBeginAsync(Event, default));
        Assert.Equal(WebhookEventClaim.InProgress, await dedup.TryBeginAsync(Event, default));
        await dedup.CompleteAsync(Event, default);
        Assert.Equal(WebhookEventClaim.AlreadyProcessed, await dedup.TryBeginAsync(Event, default));
    }

    [Fact]
    public async Task AnAbandonedEventCanBeProcessedAgain()
    {
        var dedup = new InMemoryWebhookEventDeduplicator();

        await dedup.TryBeginAsync(Event, default);
        await dedup.AbandonAsync(Event, default);

        Assert.Equal(WebhookEventClaim.New, await dedup.TryBeginAsync(Event, default));
    }

    [Fact]
    public async Task AStaleClaimExpiresAndOldIdsArePruned()
    {
        var clock = new FakeTimeProvider();
        var dedup = new InMemoryWebhookEventDeduplicator(clock) { Retention = TimeSpan.FromHours(1), InProgressTimeout = TimeSpan.FromMinutes(1) };

        await dedup.TryBeginAsync(Event, default);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(WebhookEventClaim.New, await dedup.TryBeginAsync(Event, default));

        await dedup.CompleteAsync(Event, default);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(WebhookEventClaim.New, await dedup.TryBeginAsync(Event, default));
    }
}
