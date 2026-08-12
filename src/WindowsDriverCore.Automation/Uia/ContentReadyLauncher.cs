using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using WindowsDriverCore.Platform.Applications;

namespace WindowsDriverCore.Automation.Uia;

/// <summary>
/// Holds a launch back until the application has something a client can address.
/// </summary>
/// <remarks>
/// <para>
/// <b>MEASURED 2026-08-12, and this is a race the client loses.</b> A launch
/// returns as soon as a WINDOW exists, but a packaged application's
/// <c>ApplicationFrameWindow</c> arrives before its content. From the guest
/// transcript at <c>4b1c5da</c>:
/// </para>
/// <code>
/// 14:31:10.382  launch Calculator -> window 0x4002C (ApplicationFrameWindow) 326.2 ms
/// 14:31:10.382  POST /session -> 200
/// 14:31:10.408  find AutomationId='AppName' -> 0 match(es)      &lt;- 26 ms later
/// </code>
/// <para>
/// Counted over that run, <c>AppName</c> had 13 hits and 6 misses. The misses
/// took the suite's orphaned-session helper down with them, and its exception
/// then masqueraded as four different <c>*Error_NoSuchWindow</c> failures.
/// WinAppDriver passes those because it is slow enough to wait by accident.
/// </para>
/// <para>
/// <b>The wait is on a CONDITION and it never refuses.</b> Refusing a frame with
/// no content was measured before at 130 against 150 — it cost twenty tests — so
/// this proceeds regardless when the budget runs out. A slow application still
/// gets a session; it just does not get one before its own UI exists.
/// </para>
/// <para>
/// <b>Why "has an addressable element" and not "has children".</b> Measured over
/// eight launches of two applications: children appear 12-40 ms after the frame,
/// but the element a client actually asks for lands a further 11-41 ms later.
/// Waiting on children looks principled and stays racy, so the condition is a
/// descendant carrying a non-empty AutomationId — the same thing a client
/// searches by.
/// </para>
/// </remarks>
public sealed class ContentReadyLauncher : IApplicationLauncher
{
    /// <summary>How long to wait before giving up and answering anyway.</summary>
    /// <remarks>
    /// <para>
    /// <b>Taken from the REFERENCE, not from our own timings.</b> Measured on the
    /// guest over four rounds: WinAppDriver answers <c>POST /session</c> in
    /// 771-1515 ms, averaging 1118, while this driver averages 392 ms. So there
    /// is roughly 700 ms of headroom in which we can keep waiting for the
    /// application's UI and still answer sooner than the reference does.
    /// </para>
    /// <para>
    /// The first version of this was 500 ms, chosen as eight times OUR measured
    /// 28-59 ms content gap. That is the wrong way round: a budget inferred from
    /// our own speed says nothing about when a client would have got an answer
    /// from WinAppDriver, and one <c>AppName</c> lookup still missed at 500 ms.
    /// Failing sooner than the reference is a different result, not a faster one.
    /// </para>
    /// <para>
    /// Still bounded, and it still proceeds rather than refusing: an application
    /// that never renders must not hold a session hostage, and refusing an empty
    /// frame was measured at 130 against 150.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1000);

    private readonly IApplicationLauncher _inner;
    private readonly IUIAutomation _automation;
    private readonly TimeProvider _time;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The launcher that actually starts the application.</param>
    /// <param name="automation">Used to ask whether the window has content yet.</param>
    /// <param name="time">Bounds the wait.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ContentReadyLauncher(
        IApplicationLauncher inner, IUIAutomation automation, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(time);

        _inner = inner;
        _automation = automation;
        _time = time;
    }

    /// <inheritdoc />
    public LaunchResult Launch(ApplicationTarget target)
    {
        LaunchResult launched = _inner.Launch(target);

        if (launched.Application is null)
        {
            // Nothing started, so there is nothing to wait for and the failure
            // must travel back unchanged.
            return launched;
        }

        WaitForAddressableContent(launched.Application.WindowHandle);

        return launched;
    }

    private void WaitForAddressableContent(nint window)
    {
        long deadline = _time.GetTimestamp() +
            (long)(Budget.TotalSeconds * _time.TimestampFrequency);

        while (_time.GetTimestamp() < deadline)
        {
            if (HasAddressableContent(window))
            {
                return;
            }
        }
    }

    private bool HasAddressableContent(nint window)
    {
        IUIAutomationElement? root;
        try
        {
            root = _automation.ElementFromHandle(window);
        }
        catch (COMException)
        {
            // The window went away while we waited. Not this decorator's
            // problem to report - the caller finds out on its next command.
            return true;
        }

        if (root is null)
        {
            return true;
        }

        try
        {
            // "AutomationId is not empty", which is what a client searches by.
            //
            // Released through the local guard rather than ComScope: ComScope
            // calls ReleaseComObject unconditionally, which throws for a
            // substituted condition, and teaching a type used across the whole
            // automation layer a new tolerance to suit one caller is a change to
            // shared meaning rather than a local fix.
            IUIAutomationCondition anonymous =
                _automation.CreatePropertyCondition(UiaPropertyIds.AutomationId, string.Empty);
            IUIAutomationCondition addressable = _automation.CreateNotCondition(anonymous);

            try
            {
                IUIAutomationElement? found =
                    root.FindFirst(TreeScope.TreeScope_Descendants, addressable);

                if (found is null)
                {
                    return false;
                }

                Release(found);
                return true;
            }
            finally
            {
                Release(addressable);
                Release(anonymous);
            }
        }
        catch (COMException)
        {
            // A provider that cannot answer yet is not a provider with content.
            return false;
        }
        finally
        {
            Release(root);
        }
    }

    /// <summary>Releases a COM element, tolerating one that is not COM at all.</summary>
    /// <remarks>
    /// <c>ReleaseComObject</c> throws <c>ArgumentException</c> for anything that
    /// is not a runtime callable wrapper, and this type is public: a test can
    /// legitimately hand it a substituted element. The same guard is written
    /// into <c>CachingElementResolver</c> for the same reason.
    /// </remarks>
    private static void Release(object element)
    {
        if (Marshal.IsComObject(element))
        {
            Marshal.ReleaseComObject(element);
        }
    }
}
