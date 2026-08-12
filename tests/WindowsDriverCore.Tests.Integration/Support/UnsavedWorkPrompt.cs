using System.Collections.Generic;
using System;
using System.Threading;
using Interop.UIAutomationClient;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// Answers "Don't save" to the prompt an application shows when it is asked to
/// close with unsaved work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Teardown, not a driver feature.</b> A close request is the polite way to
/// end an application and it is also what raises the prompt, so a suite that
/// closes applications has to be able to answer one. Leaving it up costs the
/// full <c>WaitForExit</c> and then a kill — and for the whole of that wait a
/// modal dialog owns the foreground on a desktop several suites share, where a
/// stolen focus turns another run's synthesized click into input for whatever is
/// now in front.
/// </para>
/// <para>
/// <b>The prompt is inside the application's own window.</b> Measured
/// 2026-08-11 against Notepad on Windows 11 26200: it is a WinUI
/// <c>ContentDialog</c> in the window's UIA subtree, with no HWND of its own, so
/// enumerating top-level windows cannot see it. UI Automation is the only
/// instrument that can, which is why this reaches for the same finder the driver
/// uses rather than for Win32.
/// </para>
/// <para>
/// <b>Matched on the automation id, not the label.</b> A <c>ContentDialog</c>'s
/// three buttons are <c>PrimaryButton</c>, <c>SecondaryButton</c> and
/// <c>CloseButton</c> whatever they say, so the id survives a localized Windows
/// while "Don't save" does not. The consequence of matching the wrong one here is
/// a Save dialog rather than a closed application, so the id is also the safer
/// of the two.
/// </para>
/// <para>
/// A classic Win32 save prompt — a separate owned dialog, as on Windows 10 —
/// is NOT handled. It has not been measured on this machine and inventing a
/// locator for it would be a guess dressed as a fix. The caller's kill remains
/// the backstop there, exactly as before.
/// </para>
/// </remarks>
internal static class UnsavedWorkPrompt
{
    /// <summary>The discard button of a WinUI content dialog.</summary>
    private const string DiscardButtonId = "SecondaryButton";

    /// <summary>The discard button of a classic Win32 dialog.</summary>
    /// <remarks>
    /// Matched by LABEL rather than automation id, because a native BUTTON has
    /// no automation id worth the name - UIA reports its control id. That is
    /// safe only because this label belongs to the repository's own Win32
    /// subject and does not localize. A third-party dialog would need a
    /// different approach, and inventing one without a subject to measure
    /// against would be a guess.
    /// </remarks>
    private const string DiscardButtonLabel = "Discard changes";

    /// <summary>
    /// How long to keep watching for a prompt that may never appear.
    /// </summary>
    /// <remarks>
    /// It bounds only the case where the window neither closes nor prompts. An
    /// application that simply exits ends the wait the moment its window is
    /// gone, and one that prompts ends it the moment the button appears — so
    /// this number is not on the path of either normal outcome.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Waits for a discard prompt on a window that has been asked to close, and
    /// answers it.
    /// </summary>
    /// <param name="window">The window that was asked to close.</param>
    /// <returns>
    /// True if a prompt was found and discarded; false if the window closed
    /// without one, which is the ordinary case.
    /// </returns>
    internal static bool DiscardIfAsked(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        WindowLocator windows = new();
        CUIAutomationClass automation = new();
        UiaElementResolver resolver = new(automation);
        UiaElementFinder finder = new(automation, resolver);
        UiaElementInteractor interactor = new(automation, resolver);

        string? discard = null;
        nint host = window;

        // Three exits, and only the last is a timeout: the button appeared, the
        // window went away on its own, or nothing happened at all.
        SpinWait.SpinUntil(
            () =>
            {
                if (!windows.Exists(window))
                {
                    return true;
                }

                // TWO SHAPES, and a helper that knows one hangs on the other.
                //
                // A WinUI ContentDialog has no window of its own and lives in
                // its owner's UIA subtree, so it is found by searching the
                // window's descendants. A classic Win32 dialog is a SEPARATE
                // owned top-level window - a sibling under the desktop - which
                // that search never reaches. Measured: a teardown that only knew
                // the first spent the full kill wait on the second.
                FindResult inWindow = finder.FindFirst(
                    window, LocatorKind.AutomationId, DiscardButtonId);

                if (inWindow.ElementIds.Count > 0)
                {
                    host = window;
                    discard = inWindow.ElementIds[0];
                    return true;
                }

                // BY LABEL, still inside the window. An OWNED dialog is a
                // separate HWND but UI Automation nests it under its owner, so
                // the descendant search reaches it - the same shape measured for
                // a WinUI context menu, whose PopupHost owns an HWND and is
                // still a UIA child of the application frame. Cheaper than
                // enumerating the desktop, and tried before it.
                FindResult labelled = finder.FindFirst(
                    window, LocatorKind.Name, DiscardButtonLabel);

                if (labelled.ElementIds.Count > 0)
                {
                    host = window;
                    discard = labelled.ElementIds[0];
                    return true;
                }

                foreach (nint owned in OwnedDialogsOf(windows, window))
                {
                    FindResult inDialog = finder.FindFirst(
                        owned, LocatorKind.Name, DiscardButtonLabel);

                    if (inDialog.ElementIds.Count > 0)
                    {
                        host = owned;
                        discard = inDialog.ElementIds[0];
                        return true;
                    }
                }

                return false;
            },
            Budget);

        return discard is not null &&
            interactor.Click(host, discard).Outcome == ElementActionOutcome.Performed;
    }

    /// <summary>Top-level windows owned by the same process as <paramref name="window"/>.</summary>
    /// <remarks>
    /// A classic dialog is a separate window, so finding it means looking beside
    /// the application rather than inside it. Scoped to the same process so a
    /// teardown never reaches into somebody else's dialog - this runs on a
    /// desktop several suites share.
    /// </remarks>
    private static IEnumerable<nint> OwnedDialogsOf(WindowLocator windows, nint window)
    {
        int owner = windows.GetOwningProcessId(window);

        if (owner == 0)
        {
            yield break;
        }

        foreach (nint candidate in MainWindowWaiter.SnapshotTopLevelWindows())
        {
            if (candidate != window &&
                windows.Exists(candidate) &&
                windows.GetOwningProcessId(candidate) == owner)
            {
                yield return candidate;
            }
        }
    }
}
