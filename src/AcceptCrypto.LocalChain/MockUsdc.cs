using System.Numerics;
using System.Reflection;
using System.Text.Json;
using AcceptCrypto.Domain.Money;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;

namespace AcceptCrypto.LocalChain;

/// <summary>Compiled MockUsdc artifact (ABI + creation bytecode), embedded from <c>Contracts/MockUsdc.json</c>.</summary>
public static class MockUsdcArtifact
{
    private static readonly Lazy<(string Abi, string Bytecode)> Loaded = new(Load);

    /// <summary>Contract ABI (JSON).</summary>
    public static string Abi => Loaded.Value.Abi;

    /// <summary>Creation bytecode (0x-prefixed hex).</summary>
    public static string Bytecode => Loaded.Value.Bytecode;

    private static (string, string) Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AcceptCrypto.LocalChain.MockUsdc.json")
            ?? throw new InvalidOperationException("Embedded MockUsdc.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        return (doc.RootElement.GetProperty("abi").GetRawText(), doc.RootElement.GetProperty("bytecode").GetString()!);
    }
}

/// <summary>Deployment message for the mock USDC (no constructor arguments).</summary>
public sealed class MockUsdcDeployment : ContractDeploymentMessage
{
    /// <summary>Creates the message with the embedded bytecode.</summary>
    public MockUsdcDeployment() : base(MockUsdcArtifact.Bytecode)
    {
    }
}

/// <summary><c>transfer(address,uint256)</c>.</summary>
[Function("transfer", "bool")]
public sealed class TransferFunction : FunctionMessage
{
    /// <summary>Recipient.</summary>
    [Parameter("address", "to", 1)]
    public string To { get; set; } = string.Empty;

    /// <summary>Amount in base units.</summary>
    [Parameter("uint256", "amount", 2)]
    public BigInteger Amount { get; set; }
}

/// <summary><c>mint(address,uint256)</c> — test faucet.</summary>
[Function("mint")]
public sealed class MintFunction : FunctionMessage
{
    /// <summary>Recipient.</summary>
    [Parameter("address", "to", 1)]
    public string To { get; set; } = string.Empty;

    /// <summary>Amount in base units.</summary>
    [Parameter("uint256", "amount", 2)]
    public BigInteger Amount { get; set; }
}

/// <summary><c>balanceOf(address)</c>.</summary>
[Function("balanceOf", "uint256")]
public sealed class BalanceOfFunction : FunctionMessage
{
    /// <summary>Account.</summary>
    [Parameter("address", "account", 1)]
    public string Account { get; set; } = string.Empty;
}

/// <summary><c>decimals()</c>.</summary>
[Function("decimals", "uint8")]
public sealed class DecimalsFunction : FunctionMessage;

/// <summary>ERC-20 <c>Transfer(address indexed from, address indexed to, uint256 value)</c>.</summary>
[Event("Transfer")]
public sealed class TransferEventDto : IEventDTO
{
    /// <summary>Sender.</summary>
    [Parameter("address", "from", 1, true)]
    public string From { get; set; } = string.Empty;

    /// <summary>Recipient.</summary>
    [Parameter("address", "to", 2, true)]
    public string To { get; set; } = string.Empty;

    /// <summary>Amount in base units.</summary>
    [Parameter("uint256", "value", 3, false)]
    public BigInteger Value { get; set; }
}

/// <summary>TEST-ONLY client for the mock 6-decimal USDC on a local chain.</summary>
/// <param name="web3">Web3 bound to the sending account (gas payer / signer).</param>
/// <param name="address">Token contract address.</param>
public sealed class MockUsdc(IWeb3 web3, string address)
{
    /// <summary>Token contract address.</summary>
    public string Address { get; } = address;

    /// <summary>Deploys a new mock USDC from the account of <paramref name="web3"/>.</summary>
    public static async Task<MockUsdc> DeployAsync(IWeb3 web3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(web3);
        var receipt = await web3.Eth.GetContractDeploymentHandler<MockUsdcDeployment>()
            .SendRequestAndWaitForReceiptAsync(new MockUsdcDeployment(), CancellationTokenSource.CreateLinkedTokenSource(ct))
            .ConfigureAwait(false);
        EnsureSuccess(receipt, "deploy MockUsdc");
        return new MockUsdc(web3, AddressUtil.Current.ConvertToChecksumAddress(receipt.ContractAddress));
    }

    /// <summary>
    /// Idempotent local setup: deploys the token at <paramref name="expectedAddress"/> if no code is there yet
    /// (requires the deployer's nonce to be 0, i.e. a fresh chain) and tops up <paramref name="holder"/> to at least
    /// <paramref name="minimumBalance"/>.
    /// </summary>
    /// <returns>The token client and whether it was deployed by this call.</returns>
    public static async Task<(MockUsdc Token, bool Deployed)> EnsureDeployedAsync(
        IWeb3 deployerWeb3, string expectedAddress, string holder, UsdcAmount minimumBalance, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deployerWeb3);
        var code = await deployerWeb3.Eth.GetCode.SendRequestAsync(expectedAddress).ConfigureAwait(false);
        var deployed = false;
        MockUsdc token;
        if (string.IsNullOrEmpty(code) || code == "0x")
        {
            token = await DeployAsync(deployerWeb3, ct).ConfigureAwait(false);
            deployed = true;
            if (!string.Equals(token.Address, expectedAddress, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"MockUsdc was deployed at {token.Address}, expected {expectedAddress}. The deployer account was used before " +
                    "on this chain; restart Anvil (fresh chain) or pass the actual token address.");
            }
        }
        else
        {
            token = new MockUsdc(deployerWeb3, expectedAddress);
        }

        var balance = await token.BalanceOfAsync(holder).ConfigureAwait(false);
        if (balance < minimumBalance)
        {
            await token.MintAsync(holder, minimumBalance - balance, ct).ConfigureAwait(false);
        }

        return (token, deployed);
    }

    /// <summary>Returns a client for the same token that sends from another account.</summary>
    public MockUsdc As(IWeb3 senderWeb3) => new(senderWeb3, Address);

    /// <summary>Mints test USDC (anyone may call it on the mock).</summary>
    public async Task<TransactionReceipt> MintAsync(string to, UsdcAmount amount, CancellationToken ct = default)
    {
        var receipt = await web3.Eth.GetContractTransactionHandler<MintFunction>()
            .SendRequestAndWaitForReceiptAsync(Address, new MintFunction { To = to, Amount = amount.BaseUnits }, CancellationTokenSource.CreateLinkedTokenSource(ct))
            .ConfigureAwait(false);
        return EnsureSuccess(receipt, "mint");
    }

    /// <summary>Sends <paramref name="amount"/> to <paramref name="to"/> and waits for the receipt (one block with automine).</summary>
    public async Task<TransactionReceipt> TransferAsync(string to, UsdcAmount amount, CancellationToken ct = default)
    {
        var receipt = await web3.Eth.GetContractTransactionHandler<TransferFunction>()
            .SendRequestAndWaitForReceiptAsync(Address, new TransferFunction { To = to, Amount = amount.BaseUnits }, CancellationTokenSource.CreateLinkedTokenSource(ct))
            .ConfigureAwait(false);
        return EnsureSuccess(receipt, "transfer");
    }

    /// <summary>Sends a transfer without waiting for it to be mined (e.g. with automine off).</summary>
    public Task<string> SendTransferAsync(string to, UsdcAmount amount) =>
        web3.Eth.GetContractTransactionHandler<TransferFunction>()
            .SendRequestAsync(Address, new TransferFunction { To = to, Amount = amount.BaseUnits });

    /// <summary>Token balance of <paramref name="account"/>.</summary>
    public async Task<UsdcAmount> BalanceOfAsync(string account, BlockParameter? block = null)
    {
        var units = await web3.Eth.GetContractQueryHandler<BalanceOfFunction>()
            .QueryAsync<BigInteger>(Address, new BalanceOfFunction { Account = account }, block)
            .ConfigureAwait(false);
        return UsdcAmount.FromBaseUnits(units);
    }

    /// <summary>The token's <c>decimals()</c> (6 for USDC).</summary>
    public Task<byte> DecimalsAsync() =>
        web3.Eth.GetContractQueryHandler<DecimalsFunction>().QueryAsync<byte>(Address, new DecimalsFunction());

    private static TransactionReceipt EnsureSuccess(TransactionReceipt receipt, string what)
    {
        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException($"Transaction to {what} failed (tx {receipt.TransactionHash}).");
        }

        return receipt;
    }
}
