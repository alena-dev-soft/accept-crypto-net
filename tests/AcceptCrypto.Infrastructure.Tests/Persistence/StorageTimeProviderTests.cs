using AcceptCrypto.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AcceptCrypto.Infrastructure.Tests.Persistence;

public sealed class StorageTimeProviderTests
{
    [Fact]
    public void Truncate_drops_the_sub_microsecond_ticks()
    {
        var value = new DateTimeOffset(2026, 11, 18, 5, 46, 0, TimeSpan.Zero).AddTicks(9_175_839);

        Assert.Equal(new DateTimeOffset(2026, 11, 18, 5, 46, 0, TimeSpan.Zero).AddTicks(9_175_830), StorageTimeProvider.Truncate(value));
    }

    [Fact]
    public void Now_has_microsecond_precision_like_timestamptz()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0, StorageTimeProvider.Instance.GetUtcNow().Ticks % 10);
        }
    }

    [Fact]
    public void Persistence_registers_it_as_the_default_clock()
    {
        var services = new ServiceCollection().AddAcceptCryptoPersistence("Host=localhost");

        Assert.Same(StorageTimeProvider.Instance, services.BuildServiceProvider().GetRequiredService<TimeProvider>());
    }
}
