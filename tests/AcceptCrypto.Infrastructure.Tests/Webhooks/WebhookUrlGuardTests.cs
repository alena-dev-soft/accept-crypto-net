using System.Net;
using System.Net.Sockets;
using System.Text;
using AcceptCrypto.Infrastructure.Webhooks;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Tests.Webhooks;

/// <summary>SSRF protection of webhook URLs (NFR-6).</summary>
public sealed class WebhookUrlGuardTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.255.255.254", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Classifies_addresses(string address, bool isPublic) =>
        Assert.Equal(isPublic, WebhookUrlGuard.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task Host_resolving_to_public_addresses_is_allowed()
    {
        var guard = Guard(new FakeResolver { ["merchant.example"] = ["93.184.216.34", "2606:4700:4700::1111"] });

        var check = await guard.CheckAsync(new Uri("https://merchant.example/hook"));

        Assert.True(check.Allowed, check.Reason);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("93.184.216.34,192.168.0.10")] // one private answer is enough to refuse
    public async Task Host_resolving_to_internal_addresses_is_refused(string answers)
    {
        var guard = Guard(new FakeResolver { ["evil.example"] = answers.Split(',') });

        var check = await guard.CheckAsync(new Uri("https://evil.example/hook"));

        Assert.False(check.Allowed);
        Assert.Contains("private, loopback", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unresolvable_host_and_localhost_are_refused()
    {
        var guard = Guard(new FakeResolver());

        Assert.False((await guard.CheckAsync(new Uri("https://nowhere.example/hook"))).Allowed);
        Assert.False((await guard.CheckAsync(new Uri("https://localhost/hook"))).Allowed);
        Assert.False((await guard.CheckAsync(new Uri("https://api.localhost/hook"))).Allowed);
        Assert.False((await guard.CheckAsync(new Uri("http://merchant.example/hook"))).Allowed); // https required
    }

    [Fact]
    public async Task Allowlisted_host_may_be_internal_and_use_http()
    {
        var guard = Guard(new FakeResolver { ["demo-shop"] = ["172.18.0.4"] }, "demo-shop");

        Assert.True((await guard.CheckAsync(new Uri("http://demo-shop:8080/webhooks"))).Allowed);
        Assert.True((await guard.CheckAsync(new Uri("http://DEMO-SHOP./webhooks"))).Allowed);
    }

    [Fact]
    public async Task Guarded_handler_refuses_to_connect_to_loopback_even_if_dns_changes_after_the_check()
    {
        await using var server = await LocalServer.StartAsync();

        // DNS rebinding: the first answer is public (passes CheckAsync), the next one points to 127.0.0.1.
        var resolver = new FakeResolver { ["rebind.example"] = ["93.184.216.34"] };
        var guard = Guard(resolver);
        Assert.True((await guard.CheckAsync(new Uri("https://rebind.example/"))).Allowed);
        resolver["rebind.example"] = ["127.0.0.1"];

        using var http = new HttpClient(guard.CreateHttpHandler());
        var viaName = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri($"http://rebind.example:{server.Port}/")));
        Assert.IsType<WebhookUrlBlockedException>(viaName.InnerException);

        var viaLiteral = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/")));
        Assert.IsType<WebhookUrlBlockedException>(viaLiteral.InnerException);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Guarded_handler_connects_to_an_allowlisted_internal_host()
    {
        await using var server = await LocalServer.StartAsync();
        var guard = Guard(new FakeResolver { ["demo-shop"] = ["127.0.0.1"] }, "demo-shop");

        using var http = new HttpClient(guard.CreateHttpHandler());
        using var response = await http.GetAsync(new Uri($"http://demo-shop:{server.Port}/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public void Syntax_check_needs_no_dns()
    {
        var options = new WebhookOptions();
        Assert.Null(WebhookUrlGuard.CheckSyntax(new Uri("https://merchant.example/hook"), options));
        Assert.NotNull(WebhookUrlGuard.CheckSyntax(new Uri("https://192.168.1.10/hook"), options));
        Assert.NotNull(WebhookUrlGuard.CheckSyntax(new Uri("https://merchant.example/hook#x"), options));
        Assert.NotNull(WebhookUrlGuard.CheckSyntax(new Uri("/relative", UriKind.Relative), options));
        options.AllowedHosts.Add("192.168.1.10");
        Assert.Null(WebhookUrlGuard.CheckSyntax(new Uri("http://192.168.1.10/hook"), options));
    }

    private static WebhookUrlGuard Guard(IHostAddressResolver resolver, params string[] allowed)
    {
        var options = new WebhookOptions();
        foreach (var host in allowed)
        {
            options.AllowedHosts.Add(host);
        }

        return new WebhookUrlGuard(new StaticOptionsMonitor(options), resolver);
    }

    private sealed class FakeResolver : IHostAddressResolver, System.Collections.IEnumerable
    {
        private readonly Dictionary<string, IPAddress[]> _answers = new(StringComparer.OrdinalIgnoreCase);

        public string[] this[string host]
        {
            set => _answers[host] = value.Select(IPAddress.Parse).ToArray();
        }

        public void Add(string host, string[] addresses) => this[host] = addresses;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            _answers.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new SocketException((int)SocketError.HostNotFound);

        public System.Collections.IEnumerator GetEnumerator() => _answers.GetEnumerator();
    }

    private sealed class StaticOptionsMonitor(WebhookOptions value) : IOptionsMonitor<WebhookOptions>
    {
        public WebhookOptions CurrentValue => value;

        public WebhookOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<WebhookOptions, string?> listener) => null;
    }

    /// <summary>A minimal HTTP server on 127.0.0.1 that counts connections.</summary>
    private sealed class LocalServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        private int _connections;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref _connections);

        public static Task<LocalServer> StartAsync()
        {
            var server = new LocalServer();
            server._listener.Start();
            server._loop = server.RunAsync();
            return Task.FromResult(server);
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop!;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _connections);
                var stream = client.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer, _cts.Token);
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(response, _cts.Token);
            }
        }
    }
}
