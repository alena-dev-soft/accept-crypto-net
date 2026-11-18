using QRCoder;

namespace AcceptCrypto.Service.PaymentPage;

/// <summary>QR codes as SVG (scalable, crisp in screenshots, no image libraries).</summary>
internal static class QrCodes
{
    public static string Svg(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(8, "#0f172a", "#ffffff", drawQuietZones: true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
