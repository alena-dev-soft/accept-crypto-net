using System.Numerics;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.LocalChain;
using Nethereum.RPC.Eth.DTOs;

namespace AcceptCrypto.IntegrationTests.Chain;

/// <summary>The local chain toolkit against a real Anvil: mock USDC, transfers, mining, forced reorg, time warp.</summary>
[Collection(AnvilTestGroup.Name)]
public sealed class LocalChainTests(AnvilFixture chain) : IAsyncLifetime
{
    private const string DepositAddress = "0x9858EfFD232B4033E47d90003D41EC34EcaEda94"; // merchant test xpub / 0

    public Task InitializeAsync() => chain.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Setup_deploys_a_6_decimal_token_at_the_well_known_address_and_funds_the_payer()
    {
        Assert.Equal(AnvilAccounts.MockUsdcAddress, chain.Usdc.Address, ignoreCase: true);
        Assert.Equal(6, await chain.Usdc.DecimalsAsync());
        Assert.Equal(UsdcAmount.Parse("1000"), await chain.Usdc.BalanceOfAsync(AnvilAccounts.Payer.Address));
        Assert.Equal(AnvilAccounts.ChainId, await chain.Anvil.GetChainIdAsync());
    }

    [Fact]
    public async Task Transfer_emits_an_ERC20_Transfer_log_that_eth_getLogs_finds_by_recipient()
    {
        var receipt = await chain.Usdc.TransferAsync(DepositAddress, UsdcAmount.Parse("12.345678"));

        var filter = chain.PayerWeb3.Eth.GetEvent<TransferEventDto>(chain.Usdc.Address)
            .CreateFilterInput<string, string>(null!, DepositAddress, BlockParameter.CreateEarliest(), BlockParameter.CreateLatest());
        var logs = await chain.PayerWeb3.Eth.GetEvent<TransferEventDto>(chain.Usdc.Address).GetAllChangesAsync(filter);

        var log = Assert.Single(logs);
        Assert.Equal(new BigInteger(12_345_678), log.Event.Value);
        Assert.Equal(AnvilAccounts.Payer.Address, log.Event.From, ignoreCase: true);
        Assert.Equal(DepositAddress, log.Event.To, ignoreCase: true);
        Assert.Equal(receipt.TransactionHash, log.Log.TransactionHash);
        Assert.Equal(UsdcAmount.Parse("12.345678"), await chain.Usdc.BalanceOfAsync(DepositAddress));
    }

    [Fact]
    public async Task Forced_reorg_removes_the_transfer_and_replaces_the_block_hash()
    {
        var receipt = await chain.Usdc.TransferAsync(DepositAddress, UsdcAmount.Parse("5"));
        var height = (long)receipt.BlockNumber.Value;
        var before = await chain.Anvil.GetBlockAsync(height);
        await chain.Anvil.MineAsync(); // the transfer has 2 confirmations now

        await chain.Anvil.ReorgAsync(2);

        var after = await chain.Anvil.GetBlockAsync(height);
        Assert.Equal(height + 1, await chain.Anvil.GetBlockNumberAsync()); // same height, new branch
        Assert.NotEqual(before!.Hash, after!.Hash);
        Assert.Null(await chain.Anvil.GetReceiptAsync(receipt.TransactionHash));
        Assert.Equal(UsdcAmount.Zero, await chain.Usdc.BalanceOfAsync(DepositAddress));
    }

    [Fact]
    public async Task Snapshot_based_reorg_drops_transactions_after_the_snapshot()
    {
        var snapshot = await chain.Anvil.SnapshotAsync();
        var receipt = await chain.Usdc.TransferAsync(DepositAddress, UsdcAmount.Parse("1"));
        var hash = (await chain.Anvil.GetBlockAsync((long)receipt.BlockNumber.Value))!.Hash;

        await chain.Anvil.ReorgToSnapshotAsync(snapshot, blocksToMine: 3);

        Assert.NotEqual(hash, (await chain.Anvil.GetBlockAsync((long)receipt.BlockNumber.Value))!.Hash);
        Assert.Null(await chain.Anvil.GetReceiptAsync(receipt.TransactionHash));
        Assert.Equal(UsdcAmount.Zero, await chain.Usdc.BalanceOfAsync(DepositAddress));
    }

    [Fact]
    public async Task Mining_adds_confirmations_and_manual_mining_holds_transactions_until_mined()
    {
        await chain.Anvil.SetAutomineAsync(false);
        try
        {
            var txHash = await chain.Usdc.SendTransferAsync(DepositAddress, UsdcAmount.Parse("2"));
            Assert.Null(await chain.Anvil.GetReceiptAsync(txHash)); // pending, not mined

            await chain.Anvil.MineAsync(3);

            var receipt = await chain.Anvil.GetReceiptAsync(txHash);
            Assert.NotNull(receipt);
            var head = await chain.Anvil.GetBlockNumberAsync();
            Assert.Equal(3, head - (long)receipt.BlockNumber.Value + 1); // confirmations, as defined in D4
        }
        finally
        {
            await chain.Anvil.SetAutomineAsync(true);
        }
    }

    [Fact]
    public async Task Time_warp_moves_block_timestamps_forward()
    {
        var before = (await chain.Anvil.GetBlockAsync())!;

        var after = await chain.Anvil.WarpAsync(TimeSpan.FromHours(2));

        Assert.Equal(before.Number + 1, after.Number);
        Assert.True(after.Timestamp - before.Timestamp >= TimeSpan.FromHours(2));

        var exact = after.Timestamp.AddDays(1);
        await chain.Anvil.SetNextBlockTimestampAsync(exact);
        await chain.Anvil.MineAsync();
        Assert.Equal(exact, (await chain.Anvil.GetBlockAsync())!.Timestamp);
    }
}
