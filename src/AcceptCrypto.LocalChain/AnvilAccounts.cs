namespace AcceptCrypto.LocalChain;

/// <summary>A local-chain account (address + private key).</summary>
/// <param name="Address">EIP-55 address.</param>
/// <param name="PrivateKey">Hex private key. Public knowledge for Anvil accounts — TEST ONLY.</param>
public sealed record AnvilAccount(string Address, string PrivateKey);

/// <summary>
/// TEST-ONLY well-known Anvil defaults. The keys come from Anvil's public default mnemonic
/// <c>test test test test test test test test test test test junk</c> (m/44'/60'/0'/0/i) and are funded with
/// 10 000 ETH on every fresh Anvil chain. Anyone knows them: never use them on a public network.
/// </summary>
public static class AnvilAccounts
{
    /// <summary>Anvil's default chain ID.</summary>
    public const long ChainId = 31337;

    /// <summary>Default JSON-RPC URL of a local Anvil.</summary>
    public const string DefaultRpcUrl = "http://localhost:8545";

    /// <summary>Account #0 — deploys the mock USDC (its first transaction, so the contract address is fixed).</summary>
    public static readonly AnvilAccount Deployer = new(
        "0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266",
        "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80");

    /// <summary>Account #1 — the pre-funded payer (ETH for gas from Anvil, USDC minted by <c>pay setup</c>).</summary>
    public static readonly AnvilAccount Payer = new(
        "0x70997970C51812dc3A010C7d01b50e0d17dc79C8",
        "0x59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d");

    /// <summary>Account #2 — a second funded account, e.g. for tests that need another sender.</summary>
    public static readonly AnvilAccount Other = new(
        "0x3C44CdDdB6a900fa2b585dd299e03d12FA4293BC",
        "0x5de4111afa1a4b94908f83103eb1f1706367c2e68ca870fc3fb9a804cdab365a");

    /// <summary>
    /// Address of the mock USDC when it is the deployer's first transaction on a fresh chain
    /// (CREATE address of <see cref="Deployer"/> with nonce 0). The local stack configures the service with it.
    /// </summary>
    public const string MockUsdcAddress = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
}
