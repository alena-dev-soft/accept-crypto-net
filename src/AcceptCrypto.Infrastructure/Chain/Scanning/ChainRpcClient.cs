using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcceptCrypto.Infrastructure.Chain.Scanning;

/// <summary>A block header as far as the scanner needs it.</summary>
public sealed record ChainBlock(long Number, string Hash, string ParentHash, DateTimeOffset Timestamp);

/// <summary>A raw ERC-20 <c>Transfer</c> log (hex values lower case).</summary>
public sealed record TransferLog(string TxHash, int LogIndex, long BlockNumber, string BlockHash, string From, string To, BigInteger Value, bool Removed);

/// <summary>The JSON-RPC calls the scanner uses. Read-only: the service never signs or sends transactions (NFR-5).</summary>
public interface IChainRpcClient
{
    /// <summary><c>eth_chainId</c>.</summary>
    Task<long> GetChainIdAsync(CancellationToken cancellationToken = default);

    /// <summary><c>eth_blockNumber</c>.</summary>
    Task<long> GetHeadBlockNumberAsync(CancellationToken cancellationToken = default);

    /// <summary><c>eth_getBlockByNumber</c>; null when the node does not have that block (yet).</summary>
    Task<ChainBlock?> GetBlockAsync(long number, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>eth_getLogs</c> for <c>Transfer(address,address,uint256)</c> of <paramref name="token"/> in
    /// [<paramref name="fromBlock"/>, <paramref name="toBlock"/>] whose recipient (topic 2) is one of
    /// <paramref name="recipients"/>.
    /// </summary>
    Task<IReadOnlyList<TransferLog>> GetTransferLogsAsync(
        string token, long fromBlock, long toBlock, IReadOnlyCollection<string> recipients, CancellationToken cancellationToken = default);
}

/// <summary>A JSON-RPC call failed: transport error, timeout, HTTP error or a JSON-RPC error object.</summary>
public sealed class ChainRpcException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChainRpcException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ChainRpcException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ChainRpcException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates an exception for a JSON-RPC error object returned by the node.</summary>
    public ChainRpcException(string method, long code, string message)
        : base($"{method} failed: JSON-RPC error {code}: {message}")
    {
        RpcErrorCode = code;
    }

    /// <summary>The JSON-RPC error code when the node answered with an error object (e.g. range too large); null for transport errors.</summary>
    public long? RpcErrorCode { get; }
}

/// <summary>
/// Minimal JSON-RPC client over <see cref="HttpClient"/> (named client <see cref="HttpClientName"/>, base address =
/// <c>Chain:RpcUrl</c>). Errors surface as <see cref="ChainRpcException"/>; the URL is never put into messages
/// because it may contain a provider API key.
/// </summary>
public sealed class JsonRpcChainClient(IHttpClientFactory httpClientFactory) : IChainRpcClient
{
    /// <summary>Name of the configured <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "AcceptCrypto.ChainRpc";

    /// <summary>keccak256("Transfer(address,address,uint256)").</summary>
    public const string TransferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

    private long _requestId;

    /// <inheritdoc />
    public async Task<long> GetChainIdAsync(CancellationToken cancellationToken = default) =>
        ParseQuantity(await CallAsync("eth_chainId", [], cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<long> GetHeadBlockNumberAsync(CancellationToken cancellationToken = default) =>
        ParseQuantity(await CallAsync("eth_blockNumber", [], cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<ChainBlock?> GetBlockAsync(long number, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync("eth_getBlockByNumber", [ToQuantity(number), false], cancellationToken).ConfigureAwait(false);
        if (result is not JsonObject block)
        {
            return null;
        }

        return new ChainBlock(
            ParseQuantity(block["number"]),
            Lower(block["hash"]),
            Lower(block["parentHash"]),
            DateTimeOffset.FromUnixTimeSeconds(ParseQuantity(block["timestamp"])));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TransferLog>> GetTransferLogsAsync(
        string token, long fromBlock, long toBlock, IReadOnlyCollection<string> recipients, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(recipients);
        var toTopics = new JsonArray(recipients.Select(a => (JsonNode)JsonValue.Create(AddressToTopic(a))).ToArray());
        var filter = new JsonObject
        {
            ["fromBlock"] = ToQuantity(fromBlock),
            ["toBlock"] = ToQuantity(toBlock),
            ["address"] = token.ToLowerInvariant(),
            ["topics"] = new JsonArray(TransferTopic, null, toTopics),
        };
        var result = await CallAsync("eth_getLogs", [filter], cancellationToken).ConfigureAwait(false);
        if (result is not JsonArray logs)
        {
            throw new ChainRpcException("eth_getLogs returned no array.");
        }

        var transfers = new List<TransferLog>(logs.Count);
        foreach (var node in logs)
        {
            // A standard ERC-20 Transfer has 3 topics (signature, from, to) and the value as 32 bytes of data.
            if (node is not JsonObject log || log["topics"] is not JsonArray { Count: 3 } topics)
            {
                continue;
            }

            var data = Lower(log["data"]);
            if (data.Length != 66)
            {
                continue;
            }

            transfers.Add(new TransferLog(
                Lower(log["transactionHash"]),
                checked((int)ParseQuantity(log["logIndex"])),
                ParseQuantity(log["blockNumber"]),
                Lower(log["blockHash"]),
                TopicToAddress(Lower(topics[1])),
                TopicToAddress(Lower(topics[2])),
                BigInteger.Parse("0" + data[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
                log["removed"]?.GetValue<bool>() ?? false));
        }

        return transfers;
    }

    internal static string AddressToTopic(string address) => "0x" + new string('0', 24) + address[2..].ToLowerInvariant();

    private static string TopicToAddress(string topic) => "0x" + topic[^40..];

    private static string ToQuantity(long value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    private static string Lower(JsonNode? node) =>
        node?.GetValue<string>().ToLowerInvariant() ?? throw new ChainRpcException("Malformed JSON-RPC response: missing field.");

    private static long ParseQuantity(JsonNode? node)
    {
        var hex = Lower(node);
        if (!hex.StartsWith("0x", StringComparison.Ordinal) || !long.TryParse(hex.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            throw new ChainRpcException("Malformed JSON-RPC quantity.");
        }

        return value;
    }

    private async Task<JsonNode?> CallAsync(string method, JsonNode?[] parameters, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _requestId),
            ["method"] = method,
            ["params"] = new JsonArray(parameters),
        };

        JsonNode? response;
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var httpResponse = await http.PostAsJsonAsync("", request, cancellationToken).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new ChainRpcException($"{method} failed: HTTP {(int)httpResponse.StatusCode}.");
            }

            response = await httpResponse.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // HttpRequestException messages can contain the host but not the path/query; still, keep our own text.
            throw new ChainRpcException($"{method} failed: {ex.HttpRequestError}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChainRpcException($"{method} timed out.", ex);
        }
        catch (JsonException ex)
        {
            throw new ChainRpcException($"{method} returned invalid JSON.", ex);
        }

        if (response?["error"] is JsonObject error)
        {
            throw new ChainRpcException(method, error["code"]?.GetValue<long>() ?? 0, error["message"]?.GetValue<string>() ?? "unknown error");
        }

        return response?["result"];
    }
}
