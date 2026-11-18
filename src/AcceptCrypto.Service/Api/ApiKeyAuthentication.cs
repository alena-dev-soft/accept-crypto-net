using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AcceptCrypto.Infrastructure.Merchant;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Service.Api;

/// <summary>
/// API key authentication for the merchant API (FR-17): header <c>X-Api-Key</c>, compared in constant time.
/// The key is never logged; failures are answered with RFC 9457 Problem Details.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<MerchantOptions> merchant,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string MerchantPolicy = "Merchant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0 || string.IsNullOrEmpty(values[0]))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (values.Count > 1 || !Matches(values[0]!, merchant.CurrentValue.ApiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "merchant")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync().ConfigureAwait(false);
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        var missing = result.None;
        await WriteProblemAsync(
            StatusCodes.Status401Unauthorized,
            missing ? ApiErrorCodes.ApiKeyMissing : ApiErrorCodes.ApiKeyInvalid,
            "Unauthorized",
            missing ? $"Send the merchant API key in the {HeaderName} header." : "The API key is not valid.").ConfigureAwait(false);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return WriteProblemAsync(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "Forbidden", "Access denied.");
    }

    private async Task WriteProblemAsync(int status, string code, string title, string detail)
    {
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = ApiProblems.TypeFor(code),
                Extensions = { ["code"] = code },
            },
        }).ConfigureAwait(false);
    }

    // Hashing first gives equal-length inputs, so FixedTimeEquals leaks neither content nor length.
    private static bool Matches(string presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
