using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.IntegrationTests.Api;

/// <summary>FR-0 / NFR-5 / NFR-6: the service refuses to start with bad merchant configuration, without echoing secrets.</summary>
public sealed class ConfigurationValidationTests
{
    [Theory]
    [InlineData("Merchant:ApiKey", "", "Merchant:ApiKey is required")]
    [InlineData("Merchant:ApiKey", "short-key-12345", "Merchant:ApiKey must be at least 32")]
    [InlineData("Merchant:WebhookSecret", "", "Merchant:WebhookSecret is required")]
    [InlineData("Merchant:PreviousWebhookSecret", TestMerchant.WebhookSecret, "must differ")]
    [InlineData("Merchant:Xpub", "", "Merchant:Xpub is required")]
    [InlineData("Merchant:Xpub", "xprv9s21ZrQH143K3QTDL4LXw2F7HEK3wJUD2nW2nRk4stbPy6cq3jPPqjiChkVvvNKmPGJxWUtg6LnF5kejMRNNU3TGtRBeJgk33yuGBxrMPHi", "private keys are refused")]
    [InlineData("Merchant:Xpub", "not-an-xpub", "not a valid extended public key")]
    [InlineData("Merchant:WebhookUrl", "", "Merchant:WebhookUrl is required")]
    [InlineData("Merchant:WebhookUrl", "ftp://merchant.example/hook", "must use https")]
    [InlineData("Merchant:WebhookUrl", "http://merchant.example/hook", "must use https unless")]
    [InlineData("Merchant:WebhookUrl", "https://user:pass@merchant.example/hook", "credentials")]
    [InlineData("Merchant:WebhookUrl", "https://127.0.0.1/hook", "private, loopback")]
    [InlineData("Merchant:WebhookUrl", "https://169.254.169.254/latest/meta-data", "private, loopback")]
    [InlineData("Merchant:WebhookUrl", "https://10.1.2.3/hook", "private, loopback")]
    [InlineData("Merchant:WebhookUrl", "https://[::1]/hook", "private, loopback")]
    [InlineData("Merchant:WebhookUrl", "https://localhost/hook", "localhost")]
    [InlineData("Invoices:MinAmount", "0.0000001", "Invoices:MinAmount")]
    [InlineData("Invoices:MaxLifetime", "00:00:30", "Invoices:")]
    [InlineData("Webhooks:InitialRetryDelay", "00:00:00", "Invalid Webhooks configuration")]
    [InlineData("Api:MaxPageSize", "0", "Invalid Api configuration")]
    public void Startup_fails_on_invalid_configuration(string key, string value, string expectedMessage)
    {
        var settings = TestMerchant.Settings();
        settings[key] = value;

        var error = StartupError(settings);

        Assert.NotNull(error);
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestMerchant.ApiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestMerchant.WebhookSecret, error.Message, StringComparison.Ordinal);
        if (key != "Merchant:WebhookUrl" && value.Length > 0)
        {
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("http://demo-shop:8080/webhooks", "demo-shop")]
    [InlineData("http://localhost:5080/webhooks", "localhost")]
    [InlineData("http://172.18.0.5:8080/webhooks", "172.18.0.5")]
    public void Allowlisted_internal_webhook_hosts_are_accepted(string url, string host)
    {
        var settings = TestMerchant.Settings();
        settings["Merchant:WebhookUrl"] = url;
        settings["Webhooks:AllowedHosts:0"] = host;

        Assert.Null(StartupError(settings));
    }

    [Fact]
    public void Rotation_with_a_previous_secret_is_accepted()
    {
        var settings = TestMerchant.Settings();
        Assert.Null(StartupError(settings)); // TestMerchant already sets current + previous
    }

    private static OptionsValidationException? StartupError(Dictionary<string, string?> settings)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            foreach (var (k, v) in settings)
            {
                b.UseSetting(k, v);
            }
        });

        try
        {
            using var client = factory.CreateClient();
            return null;
        }
        catch (Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is OptionsValidationException ove)
                {
                    return ove;
                }

                if (e is AggregateException agg && agg.InnerExceptions.OfType<OptionsValidationException>().FirstOrDefault() is { } inner)
                {
                    return inner;
                }
            }

            throw;
        }
    }
}
