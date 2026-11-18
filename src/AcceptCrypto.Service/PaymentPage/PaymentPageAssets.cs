using System.Collections.Frozen;
using System.Security.Cryptography;

namespace AcceptCrypto.Service.PaymentPage;

/// <summary>
/// The page's CSS and JS, embedded in the assembly (no static-file middleware, no CDN). Separate files instead of
/// inline code so the page can run under a strict Content-Security-Policy. URLs carry a content hash for caching.
/// </summary>
internal static class PaymentPageAssets
{
    private static readonly FrozenDictionary<string, (byte[] Content, string ContentType, string Version)> Files = Load();

    public static string Url(string name) => $"/pay/assets/{name}?v={Files[name].Version}";

    public static IResult Serve(string name, HttpContext http)
    {
        if (!Files.TryGetValue(name, out var file))
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "public, max-age=3600";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(file.Content, file.ContentType);
    }

    private static FrozenDictionary<string, (byte[], string, string)> Load()
    {
        var assembly = typeof(PaymentPageAssets).Assembly;
        var files = new Dictionary<string, (byte[], string, string)>(StringComparer.Ordinal);
        foreach (var (name, type) in new[] { ("pay.css", "text/css; charset=utf-8"), ("pay.js", "text/javascript; charset=utf-8") })
        {
            using var stream = assembly.GetManifestResourceStream($"AcceptCrypto.Service.PaymentPage.Assets.{name}")
                ?? throw new InvalidOperationException($"Embedded payment page asset '{name}' is missing.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            files[name] = (bytes, type, Convert.ToHexStringLower(SHA256.HashData(bytes))[..12]);
        }

        return files.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
