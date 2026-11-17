using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Webhooks;

/// <summary>Resolves host names (abstracted so tests can simulate DNS answers).</summary>
public interface IHostAddressResolver
{
    /// <summary>All addresses of <paramref name="host"/>.</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>System DNS.</summary>
public sealed class SystemHostAddressResolver : IHostAddressResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Result of a webhook URL check.</summary>
/// <param name="Allowed">True if the URL may be called.</param>
/// <param name="Reason">Why it was refused (never contains secrets).</param>
public sealed record WebhookUrlCheck(bool Allowed, string? Reason)
{
    /// <summary>The URL may be called.</summary>
    public static WebhookUrlCheck Ok { get; } = new(true, null);

    /// <summary>Refused for <paramref name="reason"/>.</summary>
    public static WebhookUrlCheck Refused(string reason) => new(false, reason);
}

/// <summary>Thrown by the guarded HTTP handler when a webhook host resolves only to blocked addresses.</summary>
public sealed class WebhookUrlBlockedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WebhookUrlBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public WebhookUrlBlockedException()
        : this("The webhook host resolves to a blocked address.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public WebhookUrlBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// SSRF protection for outgoing webhooks (NFR-6). A webhook may only reach public unicast addresses; private,
/// loopback, link-local (incl. cloud metadata 169.254.169.254), CGNAT, multicast, reserved and documentation ranges
/// are refused <b>after DNS resolution</b>, unless the host is on <see cref="WebhookOptions.AllowedHosts"/>.
/// </summary>
/// <remarks>
/// Three layers: <see cref="CheckSyntax"/> at startup (scheme, credentials, IP literals, localhost),
/// <see cref="CheckAsync"/> (resolves the host, every address must be public), and <see cref="CreateHttpHandler"/>,
/// whose connect callback re-resolves and connects only to an address it has just checked — so a DNS answer that
/// changes between check and request (DNS rebinding) cannot reach the internal network. Redirects are not followed.
/// </remarks>
public sealed class WebhookUrlGuard(IOptionsMonitor<WebhookOptions> options, IHostAddressResolver resolver)
{
    /// <summary>
    /// Checks what can be checked without DNS: absolute http(s) URL, no user info, https unless the host is
    /// allowlisted, and no blocked IP literal / localhost name unless allowlisted.
    /// </summary>
    public static string? CheckSyntax(Uri? url, WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (url is null || !url.IsAbsoluteUri)
        {
            return "must be an absolute URL";
        }

        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            return "must use https (or http for an allowlisted host)";
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return "must not contain credentials";
        }

        if (!string.IsNullOrEmpty(url.Fragment))
        {
            return "must not contain a fragment";
        }

        var host = url.IdnHost;
        if (options.IsAllowlisted(host))
        {
            return null;
        }

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return "must use https unless the host is on Webhooks:AllowedHosts";
        }

        if (IsLocalhostName(host))
        {
            return "points to localhost; add the host to Webhooks:AllowedHosts for local testing";
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublicAddress(ip))
        {
            return "points to a private, loopback, link-local or reserved address; add the host to Webhooks:AllowedHosts to allow it";
        }

        return null;
    }

    /// <summary>
    /// True for public unicast addresses. Blocks: unspecified, loopback, RFC 1918, CGNAT (100.64/10), link-local
    /// (169.254/16, fe80::/10), IETF/benchmark/documentation ranges, multicast, reserved (240/4), broadcast, IPv6 ULA
    /// (fc00::/7), site-local, and IPv4 embedded in IPv6 (mapped, NAT64, 6to4) when the embedded address is blocked.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0                                  // 0.0.0.0/8 "this network"
                || b[0] == 10                                     // 10/8 private
                || (b[0] == 100 && (b[1] & 0xC0) == 64)           // 100.64/10 CGNAT
                || b[0] == 127                                    // loopback
                || (b[0] == 169 && b[1] == 254)                   // link-local, cloud metadata
                || (b[0] == 172 && (b[1] & 0xF0) == 16)           // 172.16/12 private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)        // 192.0.0/24 IETF
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)        // TEST-NET-1
                || (b[0] == 192 && b[1] == 88 && b[2] == 99)      // 6to4 relay anycast
                || (b[0] == 192 && b[1] == 168)                   // 192.168/16 private
                || (b[0] == 198 && (b[1] & 0xFE) == 18)           // 198.18/15 benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)     // TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)      // TEST-NET-3
                || b[0] >= 224);                                  // multicast, reserved, broadcast
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (b[0] & 0xFE) == 0xFC                                         // fc00::/7 unique local
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) // 2001:db8::/32 documentation
            || (b[0] == 0x01 && b[1] == 0x00 && b.AsSpan(2, 6).IndexOfAnyExcept((byte)0) < 0)) // 100::/64 discard
        {
            return false;
        }

        // IPv4-compatible (::a.b.c.d), NAT64 (64:ff9b::/96) and 6to4 (2002::/16) embed an IPv4 address.
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
        {
            return IsPublicAddress(new IPAddress(b.AsSpan(12, 4)));
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
        {
            return IsPublicAddress(new IPAddress(b.AsSpan(12, 4)));
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicAddress(new IPAddress(b.AsSpan(2, 4)));
        }

        return true;
    }

    /// <summary>Full check including DNS: every resolved address must be public unless the host is allowlisted.</summary>
    public async Task<WebhookUrlCheck> CheckAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var current = options.CurrentValue;
        var syntax = CheckSyntax(url, current);
        if (syntax is not null)
        {
            return WebhookUrlCheck.Refused("Webhook URL " + syntax + ".");
        }

        if (current.IsAllowlisted(url.IdnHost))
        {
            return WebhookUrlCheck.Ok;
        }

        var (_, refused) = await ResolvePublicAsync(url.IdnHost, cancellationToken).ConfigureAwait(false);
        return refused is null ? WebhookUrlCheck.Ok : WebhookUrlCheck.Refused(refused);
    }

    /// <summary>
    /// An HTTP handler for webhook delivery: no redirects, no proxy, and a connect callback that refuses blocked
    /// addresses at connection time (after DNS resolution, so DNS rebinding is covered too).
    /// </summary>
    public SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    };

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] candidates;
        if (options.CurrentValue.IsAllowlisted(host))
        {
            candidates = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var (addresses, refused) = await ResolvePublicAsync(host, cancellationToken).ConfigureAwait(false);
            if (refused is not null)
            {
                throw new WebhookUrlBlockedException(refused);
            }

            candidates = addresses;
        }

        Exception? last = null;
        foreach (var address in candidates)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException("Could not connect to the webhook host.", last);
    }

    private async Task<(IPAddress[] Addresses, string? Refused)> ResolvePublicAsync(string host, CancellationToken cancellationToken)
    {
        if (IsLocalhostName(host))
        {
            return ([], "Webhook host is localhost and not on Webhooks:AllowedHosts.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return ([], "Webhook host could not be resolved.");
            }
        }

        if (addresses.Length == 0)
        {
            return ([], "Webhook host could not be resolved.");
        }

        // All addresses must be public: a mixed answer is how DNS-based SSRF usually sneaks in.
        return addresses.All(IsPublicAddress)
            ? (addresses, null)
            : ([], "Webhook host resolves to a private, loopback, link-local or reserved address and is not on Webhooks:AllowedHosts.");
    }

    private static bool IsLocalhostName(string host)
    {
        var h = host.TrimEnd('.');
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
