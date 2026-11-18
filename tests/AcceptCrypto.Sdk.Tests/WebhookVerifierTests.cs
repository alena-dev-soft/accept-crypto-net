using System.Text;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.Extensions.Time.Testing;

namespace AcceptCrypto.Sdk.Tests;

public class WebhookVerifierTests
{
    private const long Timestamp = 1763380800;
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(TestData.PaidEventJson);

    private static FakeTimeProvider ClockAt(long unixSeconds) => new(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    private static AcceptCryptoWebhookVerifier Verifier(params string[] secrets) =>
        new(secrets, timeProvider: ClockAt(Timestamp + 30));

    [Theory] // Reference vectors from docs/webhooks.md §2 (computed independently with Python hmac).
    [InlineData("whsec_test_0123456789abcdef0123456789abcdef", 1763380800, """{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid"}""", "029fe4509021b3db58be3326a42bc973e37369597e5c31fd92f8a64c3587f209")]
    [InlineData("dev_webhook_secret_0000000000000000000000", 1700000000, """{"amount":"12.5","note":"café ☕"}""", "4d09b42fe93ba66ee0d40e7aff67395927611909d7b21126ced3af625ca038d6")]
    [InlineData("previous_secret_zzzzzzzzzzzzzzzzzzzzzzzzzzzz", 1763380800, "", "4aeba15194eccfef7dfaf208ba56d5f99460256f6b5930ebd97fc83b20eee8e1")]
    public void ComputesTheDocumentedReferenceSignatures(string secret, long timestamp, string body, string expected)
    {
        var bytes = Encoding.UTF8.GetBytes(body);

        Assert.Equal(expected, AcceptCryptoWebhookVerifier.ComputeSignature(secret, timestamp, bytes));
        var verifier = new AcceptCryptoWebhookVerifier([secret], timeProvider: ClockAt(timestamp));
        Assert.Null(verifier.Verify(timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), $"v1={expected}", bytes));
    }

    [Fact]
    public void AcceptsAValidSignatureAndParsesTheEvent()
    {
        var header = AcceptCryptoWebhookVerifier.ComputeSignatureHeader([TestData.Secret], Timestamp, Body);

        var webhookEvent = Verifier(TestData.Secret).VerifyAndParse("1763380800", header, Body);

        var paid = Assert.IsType<InvoiceStatusChangedEvent>(webhookEvent);
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
    }

    [Fact]
    public void RejectsASignatureMadeWithAnotherSecret()
    {
        var forged = AcceptCryptoWebhookVerifier.ComputeSignatureHeader(["attacker_secret_0000000000000000000000000"], Timestamp, Body);

        Assert.Equal(WebhookRejectionReason.SignatureMismatch, Verifier(TestData.Secret).Verify("1763380800", forged, Body));
    }

    [Fact]
    public void RejectsAnAlteredBody()
    {
        var header = AcceptCryptoWebhookVerifier.ComputeSignatureHeader([TestData.Secret], Timestamp, Body);
        var altered = Encoding.UTF8.GetBytes(TestData.PaidEventJson.Replace("\"amount\":\"10\"", "\"amount\":\"1\"", StringComparison.Ordinal));

        Assert.Equal(WebhookRejectionReason.SignatureMismatch, Verifier(TestData.Secret).Verify("1763380800", header, altered));
    }

    [Fact]
    public void RejectsAReplayedTimestampEvenWithAValidSignature()
    {
        var header = AcceptCryptoWebhookVerifier.ComputeSignatureHeader([TestData.Secret], Timestamp, Body);
        var tenMinutesLater = new AcceptCryptoWebhookVerifier([TestData.Secret], timeProvider: ClockAt(Timestamp + 600));
        var fiveMinutesLater = new AcceptCryptoWebhookVerifier([TestData.Secret], timeProvider: ClockAt(Timestamp + 300));
        var fromTheFuture = new AcceptCryptoWebhookVerifier([TestData.Secret], timeProvider: ClockAt(Timestamp - 301));

        Assert.Equal(WebhookRejectionReason.TimestampOutsideTolerance, tenMinutesLater.Verify("1763380800", header, Body));
        Assert.Null(fiveMinutesLater.Verify("1763380800", header, Body)); // Boundary: exactly the tolerance is accepted.
        Assert.Equal(WebhookRejectionReason.TimestampOutsideTolerance, fromTheFuture.Verify("1763380800", header, Body));
    }

    [Fact]
    public void ToleranceIsConfigurable()
    {
        var header = AcceptCryptoWebhookVerifier.ComputeSignatureHeader([TestData.Secret], Timestamp, Body);
        var strict = new AcceptCryptoWebhookVerifier([TestData.Secret], TimeSpan.FromSeconds(10), ClockAt(Timestamp + 30));

        Assert.Equal(WebhookRejectionReason.TimestampOutsideTolerance, strict.Verify("1763380800", header, Body));
    }

    [Fact]
    public void RejectsASignatureForAnotherTimestamp()
    {
        var header = AcceptCryptoWebhookVerifier.ComputeSignatureHeader([TestData.Secret], Timestamp - 1, Body);

        Assert.Equal(WebhookRejectionReason.SignatureMismatch, Verifier(TestData.Secret).Verify("1763380800", header, Body));
    }

    [Fact]
    public void RotationReceiverWithNewAndOldSecretAcceptsAWebhookSignedOnlyWithTheOldSecret()
    {
        // The service has not switched yet: it still signs with the old secret only.
        var header = AcceptCryptoWebhookVerifier.ComputeSignatureHeader([TestData.OldSecret], Timestamp, Body);

        Assert.Null(Verifier(TestData.Secret, TestData.OldSecret).Verify("1763380800", header, Body));
        Assert.Equal(WebhookRejectionReason.SignatureMismatch, Verifier(TestData.Secret).Verify("1763380800", header, Body));
    }

    [Fact]
    public void RotationServiceSendingTwoSignaturesIsAcceptedByAReceiverKnowingEitherSecret()
    {
        // The service switched: "v1=<new>,v1=<old>".
        var header = AcceptCryptoWebhookVerifier.ComputeSignatureHeader([TestData.Secret, TestData.OldSecret], Timestamp, Body);

        Assert.Null(Verifier(TestData.OldSecret).Verify("1763380800", header, Body));
        Assert.Null(Verifier(TestData.Secret).Verify("1763380800", header, Body));
    }

    [Fact]
    public void IgnoresUnknownSchemesAndWhitespace()
    {
        var valid = AcceptCryptoWebhookVerifier.ComputeSignature(TestData.Secret, Timestamp, Body);

        Assert.Null(Verifier(TestData.Secret).Verify(" 1763380800 ", $"v0=deadbeef, v2={valid} , v1={valid}", Body));
        Assert.Equal(WebhookRejectionReason.SignatureMismatch, Verifier(TestData.Secret).Verify("1763380800", $"v2={valid}", Body));
        Assert.Equal(WebhookRejectionReason.SignatureMismatch, Verifier(TestData.Secret).Verify("1763380800", "v1=zz", Body));
        Assert.Equal(WebhookRejectionReason.SignatureMismatch, Verifier(TestData.Secret).Verify("1763380800", $"v1={valid[..^2]}", Body));
    }

    [Theory]
    [InlineData(null, "v1=00", WebhookRejectionReason.MissingHeaders)]
    [InlineData("1763380800", null, WebhookRejectionReason.MissingHeaders)]
    [InlineData("", "v1=00", WebhookRejectionReason.MissingHeaders)]
    [InlineData("-1763380800", "v1=00", WebhookRejectionReason.InvalidTimestamp)]
    [InlineData("1763380800.5", "v1=00", WebhookRejectionReason.InvalidTimestamp)]
    [InlineData("99999999999999999999", "v1=00", WebhookRejectionReason.InvalidTimestamp)]
    public void RejectsMissingOrInvalidHeaders(string? timestamp, string? signature, WebhookRejectionReason expected) =>
        Assert.Equal(expected, Verifier(TestData.Secret).Verify(timestamp, signature, Body));

    [Fact]
    public void VerifyAndParseThrowsWithTheReason()
    {
        var ex = Assert.Throws<AcceptCryptoWebhookException>(() => Verifier(TestData.Secret).VerifyAndParse("1763380800", "v1=00", Body));

        Assert.Equal(WebhookRejectionReason.SignatureMismatch, ex.Reason);
        Assert.DoesNotContain(TestData.Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresAtLeastOneSecret() =>
        Assert.Throws<ArgumentException>(() => new AcceptCryptoWebhookVerifier(["", null!]));
}
