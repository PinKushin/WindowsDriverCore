namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// Captures a rectangle of the screen as a PNG.
/// </summary>
/// <remarks>
/// <para>
/// <b>Screen coordinates, not window-relative.</b> The caller resolves what it
/// wants the picture of — a window's bounds, an element's bounds — and hands
/// over the rectangle. This has no notion of windows or elements, which is what
/// keeps the same implementation serving both screenshot routes.
/// </para>
/// <para>
/// <b>An interface because a protocol test must never photograph the real
/// desktop.</b> <c>WebApplicationFactory</c> boots the real container, so a
/// route left holding the live implementation would capture whatever the
/// developer had on screen — measured twice in this project for input, and a
/// capture is the same hazard pointed at the screen instead of the mouse.
/// </para>
/// </remarks>
public interface IScreenCapture
{
    /// <summary>Captures a screen rectangle and encodes it as a PNG.</summary>
    /// <param name="x">Left edge, in screen coordinates.</param>
    /// <param name="y">Top edge, in screen coordinates.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>
    /// The PNG bytes, or <see langword="null"/> if nothing could be captured —
    /// a non-positive size, or a failure in the underlying blit. Null rather
    /// than an empty array so "captured nothing" cannot be mistaken for a valid
    /// zero-byte image.
    /// </returns>
    byte[]? CapturePng(int x, int y, int width, int height);
}
