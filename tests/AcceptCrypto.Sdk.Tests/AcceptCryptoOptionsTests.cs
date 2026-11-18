using System.Net;
using AcceptCrypto.Sdk.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Sdk.Tests;

public class AcceptCryptoOptionsTests
{
    [Fact]
    public void WebhookToleranceDefaultsToFiveMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(5), new AcceptCryptoOptions().WebhookTolerance);

    [Fact]
    public void WebhookSecretsAreCurrentFirstWithoutBlanksOrDuplicates()
    {
        var options = new AcceptCryptoOptions { WebhookSecret = "new" };
        options.AdditionalWebhookSecrets.Add("old");
        options.AdditionalWebhookSecrets.Add("new");

        Assert.Equal(["new", "old"], options.GetWebhookSecrets());
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<IServiceCollection>? register = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        if (register is null)
        {
            services.AddAcceptCrypto();
        }
        else
        {
            register(services);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void OneCallBindsTheAcceptCryptoSection()
    {
        using var provider = Build(new()
        {
            ["AcceptCrypto:BaseUrl"] = "https://pay.example.com",
            ["AcceptCrypto:ApiKey"] = TestData.ApiKey,
            ["AcceptCrypto:WebhookSecret"] = TestData.Secret,
            ["AcceptCrypto:AdditionalWebhookSecrets:0"] = TestData.OldSecret,
            ["AcceptCrypto:WebhookTolerance"] = "00:02:00",
        });

        var options = provider.GetRequiredService<IOptions<AcceptCryptoOptions>>().Value;

        Assert.Equal(new Uri("https://pay.example.com"), options.BaseUrl);
        Assert.Equal(TestData.ApiKey, options.ApiKey);
        Assert.Equal([TestData.Secret, TestData.OldSecret], options.GetWebhookSecrets());
        Assert.Equal(TimeSpan.FromMinutes(2), options.WebhookTolerance);
        Assert.IsType<AcceptCryptoClient>(provider.GetRequiredService<IAcceptCryptoClient>());
        Assert.IsType<InMemoryWebhookEventDeduplicator>(provider.GetRequiredService<IWebhookEventDeduplicator>());
        Assert.NotNull(provider.GetRequiredService<AcceptCryptoWebhookVerifier>());
    }

    [Fact]
    public void ACustomSectionAndDelegateCanBeUsed()
    {
        using var provider = Build(new() { ["Payments:ApiKey"] = "from-config" }, services =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiKey"] = "section" }).Build();
            services.AddAcceptCrypto(configuration);
        });

        Assert.Equal("section", provider.GetRequiredService<IOptions<AcceptCryptoOptions>>().Value.ApiKey);
    }

    [Theory]
    [InlineData("AcceptCrypto:BaseUrl", "ftp://pay.example.com", "BaseUrl")]
    [InlineData("AcceptCrypto:ApiKey", "has space", "ApiKey")]
    [InlineData("AcceptCrypto:WebhookTolerance", "00:00:00", "WebhookTolerance")]
    [InlineData("AcceptCrypto:Timeout", "-00:00:01", "Timeout")]
    public void InvalidOptionsAreReportedWithoutSecretValues(string key, string value, string expected)
    {
        using var provider = Build(new() { [key] = value, ["AcceptCrypto:WebhookSecret"] = TestData.Secret });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AcceptCryptoOptions>>().Value);

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestData.Secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has space", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRegisteredTypedClientUsesTheConfiguredBaseUrlAndKey()
    {
        var handler = new StubHandler(_ => TestData.Json(HttpStatusCode.OK, TestData.InvoiceJson()));
        using var provider = Build(new()
        {
            ["AcceptCrypto:BaseUrl"] = "http://service:8080/acceptcrypto",
            ["AcceptCrypto:ApiKey"] = TestData.ApiKey,
        }, services => services.AddAcceptCrypto(o => o.Timeout = TimeSpan.FromSeconds(5)).ConfigurePrimaryHttpMessageHandler(() => handler));

        await provider.GetRequiredService<IAcceptCryptoClient>().GetInvoiceAsync(TestData.InvoiceId);

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal($"http://service:8080/acceptcrypto/v1/invoices/{TestData.InvoiceId}", request.RequestUri!.ToString());
        Assert.Equal(TestData.ApiKey, Assert.Single(request.Headers.GetValues("X-Api-Key")));
        Assert.StartsWith("AcceptCrypto.NET-Sdk/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }
}
