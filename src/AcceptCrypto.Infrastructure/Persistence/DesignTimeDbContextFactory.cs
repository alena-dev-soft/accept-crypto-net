using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> to build migrations. Connection string from <c>ACCEPTCRYPTO_DESIGN_CONNECTION</c>
/// (only needed for <c>database update</c>; adding a migration does not connect).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AcceptCryptoDbContext>
{
    public AcceptCryptoDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AcceptCryptoDbContext>();
        PersistenceServiceCollectionExtensions.ConfigureDbContext(
            builder,
            Environment.GetEnvironmentVariable("ACCEPTCRYPTO_DESIGN_CONNECTION") ?? "Host=localhost;Database=acceptcrypto;Username=postgres;Password=postgres");
        return new AcceptCryptoDbContext(builder.Options, Options.Create(new PersistenceOptions()));
    }
}
