using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.LocalChain;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace AcceptCrypto.IntegrationTests.Chain;

/// <summary>
/// One local Anvil chain for the chain tests. Where it comes from, in order:
/// <list type="number">
/// <item><c>ACCEPTCRYPTO_TEST_ANVIL_RPC</c> — an already running Anvil (it is RESET by the tests);</item>
/// <item>an <c>anvil</c> binary (on PATH, in <c>~/.foundry/bin</c>, or <c>ACCEPTCRYPTO_TEST_ANVIL_BIN</c>), started on a free port;</item>
/// <item>a Testcontainers container from the Foundry image (needs Docker, as in CI).</item>
/// </list>
/// </summary>
public sealed class AnvilFixture : IAsyncLifetime
{
    public const string ExternalRpcVariable = "ACCEPTCRYPTO_TEST_ANVIL_RPC";
    public const string BinaryVariable = "ACCEPTCRYPTO_TEST_ANVIL_BIN";
    public const string Image = "ghcr.io/foundry-rs/foundry:latest";

    private Process? _process;
    private IContainer? _container;

    public string RpcUrl { get; private set; } = "";

    public AnvilClient Anvil { get; private set; } = null!;

    /// <summary>Web3 signing as the pre-funded payer (Anvil account #1).</summary>
    public IWeb3 PayerWeb3 { get; private set; } = null!;

    /// <summary>Mock USDC as seen by the payer (after <see cref="ResetAsync"/>).</summary>
    public MockUsdc Usdc { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalRpcVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            RpcUrl = external;
        }
        else if (FindBinary() is { } binary)
        {
            var port = FreePort();
            _process = Process.Start(new ProcessStartInfo(binary, ["--port", port.ToString(CultureInfo.InvariantCulture), "--silent"])
            {
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Could not start anvil.");
            RpcUrl = $"http://127.0.0.1:{port}";
        }
        else
        {
            _container = new ContainerBuilder(Image)
                .WithEntrypoint("anvil")
                .WithCommand("--host", "0.0.0.0")
                .WithPortBinding(8545, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening on"))
                .Build();
            await _container.StartAsync();
            RpcUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8545)}";
        }

        Anvil = new AnvilClient(RpcUrl);
        await WaitUntilReadyAsync();
    }

    /// <summary>
    /// Fresh chain for a test: <c>anvil_reset</c> to genesis, deploy the mock USDC at its well-known address and mint
    /// 1,000 USDC to the payer — the same as <c>pay setup</c>.
    /// </summary>
    public async Task ResetAsync()
    {
        await Anvil.Client.SendRequestAsync<object?>("anvil_reset");
        var deployer = new Web3(new Account(AnvilAccounts.Deployer.PrivateKey, AnvilAccounts.ChainId), RpcUrl);
        var (token, _) = await MockUsdc.EnsureDeployedAsync(
            deployer, AnvilAccounts.MockUsdcAddress, AnvilAccounts.Payer.Address, UsdcAmount.Parse("1000"));
        // New Web3/Account: the old one may cache nonces from before the reset.
        PayerWeb3 = new Web3(new Account(AnvilAccounts.Payer.PrivateKey, AnvilAccounts.ChainId), RpcUrl);
        Usdc = token.As(PayerWeb3);
    }

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await Anvil.GetChainIdAsync();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }

    private static string? FindBinary()
    {
        var configured = Environment.GetEnvironmentVariable(BinaryVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "bin"))
            .Select(dir => Path.Combine(dir, OperatingSystem.IsWindows() ? "anvil.exe" : "anvil"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class AnvilTestGroup : ICollectionFixture<AnvilFixture>, ICollectionFixture<Persistence.PostgresFixture>
{
    public const string Name = "anvil";
}
