namespace AcceptCrypto.Sdk.Tests;

public class AcceptCryptoOptionsTests
{
    [Fact]
    public void WebhookToleranceDefaultsToFiveMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(5), new AcceptCryptoOptions().WebhookTolerance);
}
