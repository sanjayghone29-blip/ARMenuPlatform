using QRCoder;
using System.Drawing;
using System.Drawing.Imaging;

namespace ARMenuPlatform.Helpers
{
    /// <summary>
    /// Generates QR code PNG images using the QRCoder library.
    /// NuGet required: QRCoder
    ///   Install-Package QRCoder
    ///
    /// NOTE: System.Drawing (GDI+) is used for PNG rendering.
    /// On Linux/macOS hosts you must also install:
    ///   Install-Package System.Drawing.Common
    /// On Windows (Azure App Service Windows, IIS) it works out of the box.
    /// </summary>
    public static class QRCodeHelper
    {
        /// <summary>
        /// Generates a QR code PNG image for the given URL.
        ///
        /// Returns a byte[] of the rendered PNG at 300×300 pixels with a
        /// white quiet-zone border — ready to write to disk or upload directly
        /// to Azure Blob Storage.
        ///
        /// Error correction level: High (H) — tolerates up to 30% damage.
        /// This is important for printed QR codes that may get dirty or torn.
        ///
        /// Usage:
        ///   byte[] png = QRCodeHelper.GenerateQRCodePng("https://yourdomain.com/ar/menu/guid-here");
        /// </summary>
        public static byte[] GenerateQRCodePng(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL cannot be null or empty.", nameof(url));

            // ── 1. Generate the QR code data ─────────────────────────────────
            using var generator = new QRCodeGenerator();

            // ECCLevel.H = High error correction (30% data restoration capacity).
            // Use H for printed QR codes — they get scuffed, wet, torn.
            var qrData = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.H);

            // ── 2. Render to a Bitmap ────────────────────────────────────────
            using var qrCode = new QRCode(qrData);

            // pixelsPerModule: each QR module (black/white square) = 10px.
            // At ~25-30 modules per QR code, 10px/module = 250-300px output.
            // darkColor = black, lightColor = white (the quiet zone).
            // drawQuietZones = true → includes the mandatory white border.
            using var bitmap = qrCode.GetGraphic(
                pixelsPerModule: 10,
                darkColor: Color.Black,
                lightColor: Color.White,
                drawQuietZones: true);

            // ── 3. Resize to exactly 300×300 ────────────────────────────────
            // GetGraphic size varies by QR complexity. We normalise to 300×300
            // so every QR card on the page is the same size.
            using var resized = new Bitmap(300, 300);
            using var graphics = Graphics.FromImage(resized);

            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(bitmap, 0, 0, 300, 300);

            // ── 4. Encode to PNG bytes ───────────────────────────────────────
            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}