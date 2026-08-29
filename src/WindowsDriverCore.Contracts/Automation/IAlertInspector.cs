namespace WindowsDriverCore.Automation;

/// <summary>
/// The modal dialog a session is currently showing, if any.
/// </summary>
/// <remarks>
/// <para>
/// <b>WinAppDriver serves none of the alert commands</b> — measured 2026-08-29,
/// <c>alert_text</c>, <c>accept_alert</c> and <c>dismiss_alert</c> all answer
/// 404 there, and the W3C spellings likewise. So this is the *plus more* half of
/// the goal rather than a gap against the reference.
/// </para>
/// <para>
/// <b>"The alert" needs defining on a desktop, where there is no browser-modal
/// concept.</b> Here it means: a <c>Window</c> in the session window's own UIA
/// subtree whose <c>IsModal</c> is true. That covers both shapes this project has
/// actually met —
/// </para>
/// <list type="bullet">
/// <item><description>
/// a Win32 message box, which has its own HWND but is an owned pop-up and
/// therefore a UIA CHILD of the frame — measured, and the same fact that made
/// context menus findable from the session window;
/// </description></item>
/// <item><description>
/// a WinUI <c>ContentDialog</c>, which has no HWND at all and lives inside the
/// application's subtree — measured on Notepad's discard prompt.
/// </description></item>
/// </list>
/// <para>
/// <b>Scoped to the session's window rather than the desktop.</b> A modal
/// belonging to another application is not this session's to accept, and
/// answering about one would let a test dismiss a dialog it never raised.
/// </para>
/// </remarks>
public interface IAlertInspector
{
    /// <summary>The alert's message text.</summary>
    /// <param name="window">The session's window.</param>
    /// <returns>
    /// The text, or <c>NoSuchElement</c> when there is no modal dialog — which
    /// the routes turn into the protocol's "no such alert".
    /// </returns>
    ElementRead<string> Text(nint window);

    /// <summary>Presses the alert's affirmative button.</summary>
    /// <param name="window">The session's window.</param>
    /// <returns>What happened, including "there was no alert".</returns>
    ElementAction Accept(nint window);

    /// <summary>Presses the alert's negative or closing button.</summary>
    /// <param name="window">The session's window.</param>
    /// <returns>What happened, including "there was no alert".</returns>
    ElementAction Dismiss(nint window);
}
