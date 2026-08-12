using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Captures a screen rectangle with GDI and encodes it as a PNG.
/// </summary>
/// <remarks>
/// <para>
/// <b>A screen blit, which is why the caller raises the window first.</b>
/// <c>CopyFromScreen</c> reads the pixels currently on the glass, so an
/// obscured window yields whatever covers it. That is also how WinAppDriver
/// behaves — its own suite documents the capture as implicitly bringing the
/// window to the foreground — so the shared quirk is protocol parity rather
/// than a defect to engineer around.
/// </para>
/// <para>
/// <b><c>PrintWindow</c> was the alternative and is not better here.</b> It
/// asks the window to render itself, so it can capture an obscured window, but
/// it returns blank or partial output for the hardware-composited surfaces
/// these applications use. A predictable blit that matches the reference
/// driver beats an unpredictable one that sometimes reaches further.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapture : IScreenCapture
{
    /// <inheritdoc />
    public byte[]? CapturePng(int x, int y, int width, int height)
    {
        // A window can legitimately report a zero or negative size while it is
        // minimized, and Bitmap throws on one. Null says "nothing to capture",
        // which the route turns into a fault rather than a blank image.
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        using MemoryStream stream = new();
        bitmap.Save(stream, ImageFormat.Png);

        return stream.ToArray();
    }
}
