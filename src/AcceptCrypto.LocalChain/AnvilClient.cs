using System.Numerics;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;

namespace AcceptCrypto.LocalChain;

/// <summary>Head block summary.</summary>
/// <param name="Number">Block number.</param>
/// <param name="Hash">Block hash.</param>
/// <param name="Timestamp">Block timestamp (UTC).</param>
public sealed record BlockInfo(long Number, string Hash, DateTimeOffset Timestamp);

/// <summary>
/// TEST-ONLY helpers for Anvil's development RPC methods: mining, snapshots, forced reorgs and time warp.
/// Used by the integration tests and the <c>pay mine|reorg|warp</c> commands.
/// </summary>
/// <remarks>Create one per RPC URL; it is a thin, stateless wrapper over the JSON-RPC client.</remarks>
public sealed class AnvilClient(IClient client)
{
    /// <summary>Creates a client for <paramref name="rpcUrl"/>.</summary>
    public AnvilClient(string rpcUrl) : this(new Web3(rpcUrl).Client)
    {
    }

    /// <summary>Underlying JSON-RPC client.</summary>
    public IClient Client { get; } = client;

    /// <summary><c>eth_chainId</c>.</summary>
    public async Task<long> GetChainIdAsync() =>
        (long)(await Client.SendRequestAsync<HexBigInteger>("eth_chainId").ConfigureAwait(false)).Value;

    /// <summary>Current head block number.</summary>
    public async Task<long> GetBlockNumberAsync() =>
        (long)(await Client.SendRequestAsync<HexBigInteger>("eth_blockNumber").ConfigureAwait(false)).Value;

    /// <summary>Block by number (or the latest block when <paramref name="number"/> is null); null if it does not exist.</summary>
    public async Task<BlockInfo?> GetBlockAsync(long? number = null)
    {
        var tag = number is { } n ? new HexBigInteger(n).HexValue : "latest";
        var block = await Client.SendRequestAsync<BlockWithTransactionHashes?>("eth_getBlockByNumber", null, tag, false)
            .ConfigureAwait(false);
        return block is null
            ? null
            : new BlockInfo((long)block.Number.Value, block.BlockHash, DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value));
    }

    /// <summary>Receipt of a transaction, or null if it is not (or no longer) in the canonical chain.</summary>
    public Task<TransactionReceipt?> GetReceiptAsync(string txHash) =>
        Client.SendRequestAsync<TransactionReceipt?>("eth_getTransactionReceipt", null, txHash);

    /// <summary>Mines <paramref name="blocks"/> empty (or pending-tx) blocks: <c>anvil_mine</c>.</summary>
    public Task MineAsync(int blocks = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blocks, 1);
        return Client.SendRequestAsync<object?>("anvil_mine", null, new HexBigInteger(blocks).HexValue);
    }

    /// <summary>Turns automatic mining of every transaction on or off: <c>evm_setAutomine</c>.</summary>
    public Task SetAutomineAsync(bool enabled) => Client.SendRequestAsync<object?>("evm_setAutomine", null, enabled);

    /// <summary>Takes a snapshot of the chain state: <c>evm_snapshot</c>. Returns its ID.</summary>
    public Task<string> SnapshotAsync() => Client.SendRequestAsync<string>("evm_snapshot");

    /// <summary>Reverts to a snapshot (and deletes it): <c>evm_revert</c>. Returns false if the ID is unknown.</summary>
    public Task<bool> RevertAsync(string snapshotId) => Client.SendRequestAsync<bool>("evm_revert", null, snapshotId);

    /// <summary>
    /// Forced reorg: replaces the last <paramref name="depth"/> blocks with new blocks of the same height (new hashes)
    /// that do <b>not</b> contain the original transactions (<c>anvil_reorg(depth, [])</c>). Transfers mined in those
    /// blocks disappear from the canonical chain; the head height stays the same.
    /// </summary>
    public Task ReorgAsync(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        return Client.SendRequestAsync<object?>("anvil_reorg", null, depth, Array.Empty<object>());
    }

    /// <summary>
    /// Reorg via snapshots, for nodes without <c>anvil_reorg</c>: reverts to <paramref name="snapshotId"/> (taken before
    /// the transactions to drop) and mines <paramref name="blocksToMine"/> empty blocks on the new branch.
    /// </summary>
    public async Task ReorgToSnapshotAsync(string snapshotId, int blocksToMine)
    {
        if (!await RevertAsync(snapshotId).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Unknown snapshot {snapshotId}.");
        }

        if (blocksToMine > 0)
        {
            await MineAsync(blocksToMine).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Time warp: moves chain time forward by <paramref name="by"/> and mines one block carrying the new timestamp
    /// (<c>evm_increaseTime</c> + <c>anvil_mine</c>). Returns the new head.
    /// </summary>
    public async Task<BlockInfo> WarpAsync(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.FromSeconds(1));
        await Client.SendRequestAsync<object?>("evm_increaseTime", null, new HexBigInteger(new BigInteger((long)by.TotalSeconds)).HexValue)
            .ConfigureAwait(false);
        await MineAsync().ConfigureAwait(false);
        return (await GetBlockAsync().ConfigureAwait(false))!;
    }

    /// <summary>Sets the timestamp of the next mined block: <c>evm_setNextBlockTimestamp</c>.</summary>
    public Task SetNextBlockTimestampAsync(DateTimeOffset timestamp) =>
        Client.SendRequestAsync<object?>(
            "evm_setNextBlockTimestamp", null, timestamp.ToUnixTimeSeconds());
}
