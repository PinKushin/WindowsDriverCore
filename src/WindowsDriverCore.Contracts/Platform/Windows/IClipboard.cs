namespace WindowsDriverCore.Platform.Windows;

/// <summary>
/// The Windows clipboard, as <c>windows: getClipboard</c> and
/// <c>windows: setClipboard</c> reach it.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface rather than a static, because a test must never touch the
/// real one.</b> The protocol fixtures boot the whole container, so a static
/// clipboard call would overwrite whatever the person running the suite had
/// copied — the same class of accident as the pointer substitute that exists
/// because a test once clicked wherever the mouse happened to be.
/// </para>
/// <para>
/// <b>Text only.</b> The clipboard carries any format, but the protocol names
/// one: appium-windows-driver's commands are <c>{content}</c> or
/// <c>{b64Content}</c> and both decode to a string. Reporting an image as an
/// empty string would say the clipboard was empty when it was not, so a
/// non-text clipboard is a FAILED read rather than an empty one.
/// </para>
/// </remarks>
public interface IClipboard
{
    /// <summary>Reads the clipboard's text.</summary>
    /// <param name="content">The text, or null when there is none.</param>
    /// <returns>
    /// False when the clipboard could not be opened or holds no text. Distinct
    /// from an empty string, which is a clipboard that genuinely holds one.
    /// </returns>
    bool TryRead(out string? content);

    /// <summary>Replaces the clipboard's contents with text.</summary>
    /// <param name="content">The text.</param>
    /// <returns>False when the clipboard could not be opened or written.</returns>
    bool TryWrite(string content);
}
