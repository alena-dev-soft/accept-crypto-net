using AcceptCrypto.Infrastructure.Webhooks;

namespace AcceptCrypto.Infrastructure.Tests.Webhooks;

/// <summary>
/// FR-13: HMAC-SHA256 over <c>{timestamp}.{body}</c>. The expected values are reference vectors computed
/// independently (Python <c>hmac.new(secret, f"{t}.{body}".encode(), sha256).hexdigest()</c>) and published in
/// docs/webhooks.md, so the SDK and third-party receivers can test against the same numbers.
/// </summary>
public sealed class WebhookSignatureTests
{
    public static TheoryData<string, long, string, string> ReferenceVectors => new()
    {
        {
            "whsec_test_0123456789abcdef0123456789abcdef", 1763380800,
            """{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid"}""",
            "029fe4509021b3db58be3326a42bc973e37369597e5c31fd92f8a64c3587f209"
        },
        {
            // Non-ASCII body: the MAC is over the UTF-8 bytes.
            "dev_webhook_secret_0000000000000000000000", 1700000000,
            """{"amount":"12.5","note":"café ☕"}""",
            "4d09b42fe93ba66ee0d40e7aff67395927611909d7b21126ced3af625ca038d6"
        },
        {
            "previous_secret_zzzzzzzzzzzzzzzzzzzzzzzzzzzz", 1763380800, "",
            "4aeba15194eccfef7dfaf208ba56d5f99460256f6b5930ebd97fc83b20eee8e1"
        },
    };

    [Theory]
    [MemberData(nameof(ReferenceVectors))]
    public void Signature_matches_the_reference_vectors(string secret, long timestamp, string body, string expected)
    {
        Assert.Equal(expected, WebhookSignature.Compute(secret, timestamp, body));
        Assert.Equal("v1=" + expected, WebhookSignature.CreateHeader([secret], timestamp, body));
        Assert.True(WebhookSignature.Verify([secret], timestamp, body, "v1=" + expected));
    }

    [Fact]
    public void During_rotation_the_header_carries_one_signature_per_secret_current_first()
    {
        const string current = "whsec_test_0123456789abcdef0123456789abcdef";
        const string previous = "previous_secret_zzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        const string body = """{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid"}""";

        var header = WebhookSignature.CreateHeader([current, previous], 1763380800, body);

        Assert.Equal(
            "v1=029fe4509021b3db58be3326a42bc973e37369597e5c31fd92f8a64c3587f209,v1=" + WebhookSignature.Compute(previous, 1763380800, body),
            header);
        Assert.True(WebhookSignature.Verify([current], 1763380800, body, header)); // receiver already on the new secret
        Assert.True(WebhookSignature.Verify([previous], 1763380800, body, header)); // receiver still on the old secret
    }

    [Fact]
    public void Tampered_body_timestamp_secret_or_scheme_fail_verification()
    {
        const string secret = "whsec_test_0123456789abcdef0123456789abcdef";
        const string body = """{"id":"0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","type":"invoice.paid"}""";
        var header = WebhookSignature.CreateHeader([secret], 1763380800, body);

        Assert.False(WebhookSignature.Verify([secret], 1763380800, body.Replace("paid", "PAID", StringComparison.Ordinal), header));
        Assert.False(WebhookSignature.Verify([secret], 1763380801, body, header));
        Assert.False(WebhookSignature.Verify(["another_secret_aaaaaaaaaaaaaaaaaaaaaaaaaaaa"], 1763380800, body, header));
        Assert.False(WebhookSignature.Verify([secret], 1763380800, body, header.Replace("v1=", "v0=", StringComparison.Ordinal)));
        Assert.False(WebhookSignature.Verify([secret], 1763380800, body, ""));
    }
}
