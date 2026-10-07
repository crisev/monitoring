using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Monitor.Agent;

internal static class Screenshot
{
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1200;
    private const long JpegQuality = 60;

    /// <summary>All monitors as one JPEG, scaled down to fit 1920×1200. Null if the desktop can't be captured (e.g. locked).</summary>
    public static byte[]? CaptureJpeg()
    {
        try
        {
            var bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

            double scale = Math.Min(1.0, Math.Min((double)MaxWidth / bounds.Width, (double)MaxHeight / bounds.Height));
            using var image = scale < 1.0 ? Resize(full, scale) : (Bitmap)full.Clone();

            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            using var ms = new MemoryStream();
            image.Save(ms, codec, parameters);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Monitor.Core.Log.Warn($"Screenshot failed: {ex.Message}");
            return null;
        }
    }

    private static Bitmap Resize(Bitmap source, double scale)
    {
        var result = new Bitmap((int)(source.Width * scale), (int)(source.Height * scale), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(source, 0, 0, result.Width, result.Height);
        return result;
    }
}
