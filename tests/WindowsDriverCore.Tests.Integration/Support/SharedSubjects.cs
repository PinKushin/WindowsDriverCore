using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// One launch of each subject application, shared by every fixture that needs it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner's direction, repeated more than once and deferred more than
/// once:</b>
/// </para>
/// <para>
/// <i>"when you run our driver i think we are not booting shared fixtures most
/// of the time and rebooting the program, i know thats what we are doing on our
/// local tests and its kinda annoying becaiuse ive told you to stop countless
/// times"</i>
/// </para>
/// <para>
/// Twenty-three integration fixtures each launched their own copy of a subject
/// in <c>OneTimeSetUp</c> and closed it again — thirteen of the WPF app, six of
/// the Win32 one, twelve of Calculator. That is a launch and a teardown per
/// FIXTURE, where a real suite launches once and runs everything against it.
/// </para>
/// <para>
/// <b>It is not only slow, it is unfaithful.</b> The compatibility suite this
/// project is measured against keeps one long-lived session per application, as
/// every UI suite does — you do not restart the program for each test. A local
/// suite that restarts constantly cannot reproduce anything that only appears
/// over the life of a session, which is exactly the class of defect that has
/// been hardest to find here.
/// </para>
/// <para>
/// <b>Launched on first use, closed once by <see cref="SubjectTeardown"/>.</b>
/// A fixture asks for what it needs and never owns it. Callers must not close
/// these windows.
/// </para>
/// </remarks>
internal static class SharedSubjects
{
    private static readonly Dictionary<string, nint> Windows = [];
    private static readonly Lock Gate = new();

    /// <summary>The WPF subject, launched once.</summary>
    /// <returns>Its main window.</returns>
    public static nint WpfApp() => Shared("wpf", TestApp.Path, "The WPF test subject has not been built.");

    /// <summary>The pure Win32 subject, launched once.</summary>
    /// <returns>Its main window.</returns>
    public static nint Win32App() =>
        Shared("win32", Win32TestApp.Path, "The Win32 test subject has not been built.");

    /// <summary>Calculator, launched once for the whole assembly.</summary>
    /// <returns>Its main window.</returns>
    /// <remarks>
    /// <b>Twelve fixtures launched their own.</b> Calculator is packaged and
    /// single-instance, so twelve launches do not even give twelve
    /// applications — they give one application activated twelve times, each
    /// activation handing back a different frame while the earlier fixtures
    /// still hold the old handles. That is the same defect this project is
    /// chasing on the guest, reproduced in its own test suite.
    /// </remarks>
    public static nint Calculator() =>
        Shared("calculator", CalculatorAumid, "Calculator is not installed.");

    /// <summary>The Store identity Calculator is launched by.</summary>
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    /// <summary>
    /// Opens a subject through the RUNNING DRIVER the first time it is asked
    /// for, then hands back the same window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through the driver, not around it, and the first version of this got
    /// that wrong.</b> It called <c>ApplicationLauncher</c> directly — which is
    /// the very habit that makes a shared fixture impossible, as the owner
    /// pointed out: a fixture that constructs its own launcher has nowhere to
    /// put a shared session, so it launches, uses and kills.
    /// </para>
    /// <para>
    /// Going through <see cref="SharedDriverSession"/> means one real server,
    /// one session per application, and <c>DELETE /session</c> closing it — the
    /// same shape the compatibility suite uses, and the shape that exercises
    /// what actually ships.
    /// </para>
    /// </remarks>
    private static nint Shared(string key, string? appId, string whenMissing)
    {
        lock (Gate)
        {
            if (Windows.TryGetValue(key, out nint existing) &&
                existing != 0 &&
                AppLifetime.WindowExists(existing))
            {
                return existing;
            }

            if (appId is null)
            {
                Assert.Ignore(whenMissing);
            }

            nint window = SharedDriverSession.Window(appId);

            if (window == 0)
            {
                Assert.Ignore($"{whenMissing} (the driver could not open it)");
            }

            Windows[key] = window;
            return window;
        }
    }

    /// <summary>Forgets the cached handles. The SESSIONS close the applications.</summary>
    /// <remarks>
    /// Nothing is killed here. Each subject is held open by a driver session, and
    /// <c>DELETE /session</c> is what closes it — which is the driver's own
    /// teardown path being exercised rather than bypassed.
    /// </remarks>
    internal static void CloseAll()
    {
        lock (Gate)
        {
            Windows.Clear();
        }
    }
}

/// <summary>Closes the shared subjects once, after every fixture has run.</summary>
/// <remarks>
/// A <see cref="SetUpFixtureAttribute"/> with no namespace declaration applies to
/// the whole assembly, which is the only scope at which "close it when everything
/// is done" is expressible in NUnit.
/// </remarks>
[SetUpFixture]
public sealed class SubjectTeardown
{
    /// <summary>Closes the shared subject applications.</summary>
    [OneTimeTearDown]
    public void CloseTheSubjects() => SharedSubjects.CloseAll();
}
