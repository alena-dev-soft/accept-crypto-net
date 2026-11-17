using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Chain;

/// <summary>Merchant receiving setup (FR-0): the extended public key deposit addresses are derived from.</summary>
public sealed class DepositAddressOptions
{
    /// <summary>Configuration section (shared with the other merchant settings).</summary>
    public const string SectionName = "Merchant";

    /// <summary>
    /// BIP32 extended public key at <c>m/44'/60'/0'/0</c> (see <see cref="XpubDepositAddressDeriver"/>).
    /// Comes from configuration / secrets (<c>Merchant__Xpub</c>), never from code.
    /// </summary>
    public string? Xpub { get; set; }
}

/// <summary>DI registration of the deposit-address deriver.</summary>
public static class DepositAddressServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDepositAddressDeriver"/> bound to the <c>Merchant</c> section. The xpub is parsed on
    /// first use, so the host starts (and <c>/health</c> answers) even before the merchant is configured.
    /// </summary>
    public static IServiceCollection AddAcceptCryptoDepositAddresses(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<DepositAddressOptions>(configuration.GetSection(DepositAddressOptions.SectionName));
        services.AddSingleton<IDepositAddressDeriver>(sp =>
            new XpubDepositAddressDeriver(sp.GetRequiredService<IOptions<DepositAddressOptions>>().Value.Xpub ?? string.Empty));
        return services;
    }
}
