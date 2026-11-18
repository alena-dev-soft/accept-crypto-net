using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using AcceptCrypto.Infrastructure.Chain.Scanning;

namespace AcceptCrypto.Infrastructure.Tests.Chain;

public sealed class JsonRpcChainClientTests
{
    private const string Token = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
    private const string Recipient = "0x00000000000000000000000000000000D0000001";

    [Fact]
    public async Task GetTransferLogs_sends_a_recipient_topic_filter_and_parses_transfers()
    {
        var handler = new StubHandler("""
            {"jsonrpc":"2.0","id":1,"result":[
              {"address":"0x5fbdb2315678afecb367f032d93f642f64180aa3",
               "topics":["0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                         "0x00000000000000000000000070997970C51812dc3A010C7d01b50e0d17dc79C8",
                         "0x00000000000000000000000000000000000000000000000000000000d0000001"],
               "data":"0x0000000000000000000000000000000000000000000000000000000000989680",
               "blockNumber":"0x1a","blockHash":"0xAA00000000000000000000000000000000000000000000000000000000000001",
               "transactionHash":"0xBB00000000000000000000000000000000000000000000000000000000000002",
               "logIndex":"0x3","removed":false},
              {"topics":["0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef","0x01","0x02","0x03"],
               "data":"0x","blockNumber":"0x1","blockHash":"0x1","transactionHash":"0x1","logIndex":"0x0"}
            ]}
            """);
        var client = Create(handler);

        var logs = await client.GetTransferLogsAsync(Token, 16, 31, [Recipient]);

        var log = Assert.Single(logs); // the ERC-721 style log (4 topics) is skipped
        Assert.Equal("0xbb00000000000000000000000000000000000000000000000000000000000002", log.TxHash);
        Assert.Equal(3, log.LogIndex);
        Assert.Equal(26, log.BlockNumber);
        Assert.Equal("0x70997970c51812dc3a010c7d01b50e0d17dc79c8", log.From);
        Assert.Equal("0x00000000000000000000000000000000d0000001", log.To);
        Assert.Equal(new BigInteger(10_000_000), log.Value);

        var request = JsonNode.Parse(handler.LastRequest!)!;
        Assert.Equal("eth_getLogs", request["method"]!.GetValue<string>());
        var filter = request["params"]![0]!;
        Assert.Equal("0x10", filter["fromBlock"]!.GetValue<string>());
        Assert.Equal("0x1f", filter["toBlock"]!.GetValue<string>());
        Assert.Equal(Token.ToLowerInvariant(), filter["address"]!.GetValue<string>());
        Assert.Equal(JsonRpcChainClient.TransferTopic, filter["topics"]![0]!.GetValue<string>());
        Assert.Null(filter["topics"]![1]);
        Assert.Equal(
            "0x00000000000000000000000000000000000000000000000000000000d0000001",
            filter["topics"]![2]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Json_rpc_error_becomes_a_ChainRpcException_with_the_code()
    {
        var client = Create(new StubHandler("""{"jsonrpc":"2.0","id":1,"error":{"code":-32005,"message":"query returned more than 10000 results"}}"""));

        var ex = await Assert.ThrowsAsync<ChainRpcException>(() => client.GetTransferLogsAsync(Token, 0, 100_000, [Recipient]));

        Assert.Equal(-32005, ex.RpcErrorCode);
        Assert.Contains("10000 results", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_errors_and_http_errors_become_ChainRpcException_without_the_url()
    {
        var client = Create(new StubHandler("nope", HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<ChainRpcException>(() => client.GetHeadBlockNumberAsync());

        Assert.Null(ex.RpcErrorCode);
        Assert.DoesNotContain("secret-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_block_is_null_and_a_block_is_parsed()
    {
        Assert.Null(await Create(new StubHandler("""{"jsonrpc":"2.0","id":1,"result":null}""")).GetBlockAsync(99));

        var block = await Create(new StubHandler("""
            {"jsonrpc":"2.0","id":1,"result":{"number":"0x63","hash":"0xAB","parentHash":"0xCD","timestamp":"0x6553f100"}}
            """)).GetBlockAsync(99);

        Assert.Equal(new ChainBlock(99, "0xab", "0xcd", DateTimeOffset.FromUnixTimeSeconds(0x6553f100)), block);
    }

    private static JsonRpcChainClient Create(StubHandler handler) => new(new StubFactory(handler));

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://rpc.example/v2/secret-key") };
    }

    private sealed class StubHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
