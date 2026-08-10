using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Interop.UIAutomationClient;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Locators;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Windows;

namespace WindowsDriverCore.Benchmarks;

/// <summary>
/// This driver's find against FlaUI's, in the same process, on the same window.
/// </summary>
/// <remarks>
/// <para>
/// <b>FlaUI is the floor, not a competitor.</b> It reaches the same
/// <c>IUIAutomation</c> COM surface through the same interop assembly — this
/// project depends on <c>Interop.UIAutomationClient</c>, which is FlaUI's own —
/// so a difference here is this driver's layering and nothing else. There is no
/// transport on either side.
/// </para>
/// <para>
/// The number that has been quoted so far, roughly 33 ms here against roughly
/// 1070 ms through WinAppDriver, was taken under unmatched conditions: in-process
/// on one side, HTTP plus a re-resolve per iteration on the other. It is a signal
/// and has been labelled as one. This is the matched measurement, and it answers
/// the question that actually matters — <b>how much of what this driver spends is
/// UI Automation, and how much is us.</b>
/// </para>
/// <para>
/// Prediction worth writing down before the numbers exist, so it can be wrong in
/// public: the two should be close, because both are dominated by the same
/// cross-process tree walk. If this driver is much slower, the layering is the
/// problem. If it is much faster, suspect the benchmark before believing it —
/// most likely the two are not doing the same work.
/// </para>
/// </remarks>
[MemoryDiagnoser]

// In-process, deliberately. BenchmarkDotNet's default spawns a child process per
// benchmark case, and each one runs GlobalSetup — so four cases meant four
// Calculators, four UIA connections, and any setup exception swallowed inside a
// child whose output is discarded on cleanup. All four cases reported NA.
//
// The isolation a separate process buys is about JIT and runtime state at
// nanosecond scale. Everything measured here is a cross-process COM call costing
// milliseconds, which is four to six orders of magnitude above that noise floor,
// so the trade costs nothing real and makes failures visible.
[InProcess]
[WarmupCount(3)]
[IterationCount(10)]
[SuppressMessage(
    "Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification =
        "BenchmarkDotNet owns this type's lifecycle and guarantees [GlobalCleanup] runs, " +
        "which is where the disposable fields are released. Implementing IDisposable here " +
        "would add a second disposal path that BenchmarkDotNet never calls.")]
public class FindBenchmarks
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private CUIAutomationClass _automation = null!;
    private UiaElementFinder _finder = null!;
    private CachingElementResolver _cachingResolver = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;
    private string _elementId = null!;

    private UIA3Automation _flaui = null!;
    private AutomationElement _flauiWindow = null!;
    private AutomationElement _flauiElement = null!;

    /// <summary>
    /// How long setup may take before this run's numbers are refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Setup duration is the leading indicator of a machine too busy to
    /// measure on</b>, and it is worth gating on because load does not make a
    /// benchmark slow, it makes it <i>wrong</i> — and a wrong number that looks
    /// plausible is worse than no number.
    /// </para>
    /// <para>
    /// Borrowed from a sibling project, where an Appium session's setup runs
    /// 9.7 s healthy and 67 s when the machine is loaded, and the slow case
    /// reliably precedes a poisoned run. The same shape was seen here from the
    /// other direction: a find benchmark reading 11.9 ms and 16.5 ms on
    /// consecutive runs with no code change.
    /// </para>
    /// <para>
    /// Launching Calculator and finding one element is a couple of seconds on an
    /// idle machine. Ten is generous enough not to fire spuriously and far below
    /// the point where the numbers stop meaning anything.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan SetupBudget = TimeSpan.FromSeconds(10);

    [GlobalSetup]
    public void Setup()
    {
        Stopwatch startup = Stopwatch.StartNew();

        _automation = new CUIAutomationClass();
        _finder = new UiaElementFinder(_automation, new UiaElementResolver(_automation));
        _cachingResolver = new CachingElementResolver(new UiaElementResolver(_automation));
        _inspector = new UiaElementInspector(_automation, _cachingResolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(CalculatorAumid, null, null));

        if (launched.Application is null)
        {
            throw new InvalidOperationException(
                $"Calculator is required for this benchmark: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;

        FindResult found = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");
        if (found.ElementIds.Count == 0)
        {
            throw new InvalidOperationException("num5Button was not found; the app is not ready.");
        }

        _elementId = found.ElementIds[0];

        // FlaUI attaches to the same HWND this driver was given, so both subjects
        // are rooted at literally the same window rather than at two windows
        // argued to be the same.
        //
        // An earlier version searched the desktop's children for one whose
        // process id matched the launched application, and found nothing.
        // Calculator is packaged: its window belongs to ApplicationFrameHost, not
        // to CalculatorApp — the platform constraint already recorded in
        // PROJECT-KNOWLEDGE, met again from a new direction.
        _flaui = new UIA3Automation();
        _flauiWindow = _flaui.FromHandle(_window);

        // Found once and held, which is what FlaUI callers actually do and what
        // makes the read comparison fair.
        _flauiElement = _flauiWindow.FindFirstDescendant(
            condition => condition.ByAutomationId("num5Button"))
            ?? throw new InvalidOperationException("FlaUI could not find num5Button.");

        startup.Stop();
        Console.WriteLine($"setup took {startup.Elapsed.TotalSeconds:F1}s");

        if (startup.Elapsed > SetupBudget)
        {
            throw new InvalidOperationException(
                $"Setup took {startup.Elapsed.TotalSeconds:F1}s against a budget of " +
                $"{SetupBudget.TotalSeconds:F0}s. The machine is too busy to measure on, and " +
                "numbers from this run would be wrong rather than merely slow. " +
                "Stop what else is running and try again.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cachingResolver?.Dispose();
        _flaui?.Dispose();

        foreach (System.Diagnostics.Process process in
            System.Diagnostics.Process.GetProcessesByName("CalculatorApp"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>What <c>POST /element</c> does: find the first match.</summary>
    [Benchmark(Baseline = true, Description = "ours: find first by automation id")]
    public int FindThroughThisDriver() =>
        _finder.FindFirst(_window, LocatorKind.AutomationId, "num5Button").ElementIds.Count;

    /// <summary>What <c>POST /elements</c> does: find every match.</summary>
    [Benchmark(Description = "ours: find ALL by automation id")]
    public int FindAllThroughThisDriver() =>
        _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button").ElementIds.Count;

    /// <summary>
    /// The same search, but stopping at the first match, in raw COM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The experiment for why FlaUI's find is faster. <c>POST /element</c> uses
    /// <c>FindAll</c>, which must enumerate every descendant; FlaUI's
    /// <c>FindFirstDescendant</c> maps to <c>FindFirst</c>, which can return as
    /// soon as it matches. The route only ever uses the first result.
    /// </para>
    /// <para>
    /// <b>Note what this is not.</b> The obvious explanation — that reading a
    /// runtime id per match costs the difference — was measured earlier and is
    /// wrong: 47 ids cost 0.22 ms against a ~17 ms search, about 1%. That is
    /// recorded in LIMITATIONS as a wrong theory, and it cannot account for a
    /// 3.4 ms gap. If the raw FindAll case below lands near our finder, the cost
    /// is the exhaustive walk and not our layering.
    /// </para>
    /// </remarks>
    [Benchmark(Description = "raw COM: FindFirst by automation id")]
    public bool FindFirstRaw()
    {
        IUIAutomationElement root = _automation.ElementFromHandle(_window);
        IUIAutomationCondition condition =
            _automation.CreatePropertyCondition(30011, "num5Button");

        try
        {
            IUIAutomationElement? found =
                root.FindFirst(TreeScope.TreeScope_Descendants, condition);

            if (found is null)
            {
                return false;
            }

            Marshal.ReleaseComObject(found);

            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(condition);
            Marshal.ReleaseComObject(root);
        }
    }

    /// <summary>
    /// The exhaustive search in raw COM, with no runtime ids read.
    /// </summary>
    /// <remarks>
    /// The control that separates "FindAll is expensive" from "our layer around
    /// it is expensive". If this sits near our finder, the walk is the cost.
    /// </remarks>
    [Benchmark(Description = "raw COM: FindAll by automation id, no id reads")]
    public int FindAllRaw()
    {
        IUIAutomationElement root = _automation.ElementFromHandle(_window);
        IUIAutomationCondition condition =
            _automation.CreatePropertyCondition(30011, "num5Button");

        try
        {
            IUIAutomationElementArray matches =
                root.FindAll(TreeScope.TreeScope_Descendants, condition);
            int length = matches.Length;
            Marshal.ReleaseComObject(matches);

            return length;
        }
        finally
        {
            Marshal.ReleaseComObject(condition);
            Marshal.ReleaseComObject(root);
        }
    }

    /// <summary>The same find through FlaUI — the floor.</summary>
    [Benchmark(Description = "FlaUI: find by automation id")]
    public bool FindThroughFlaUi() =>
        _flauiWindow.FindFirstDescendant(
            condition => condition.ByAutomationId("num5Button")) is not null;

    /// <summary>
    /// Reading a property from an element the caller already has an id for.
    /// </summary>
    /// <remarks>
    /// The operation where this driver and FlaUI are structurally different, and
    /// the reason the handle cache exists. FlaUI holds the element; this driver
    /// is handed an opaque string by a client over HTTP and has to turn it back
    /// into an element. Measured at 19.40 ms walking against 0.45 ms cached; this
    /// says what the remaining gap to FlaUI is.
    /// </remarks>
    [Benchmark(Description = "ours: read a property by element id (cached handle)")]
    public string? ReadThroughThisDriver() => _inspector.Text(_window, _elementId).Value;

    /// <summary>The same read through FlaUI, from an element held since setup.</summary>
    /// <remarks>
    /// <b>Corrected after the first run.</b> This called
    /// <c>FindFirstDescendant(...).Name</c>, re-finding the element every
    /// iteration, and measured 8.9 ms — a find, not a read. It made this driver
    /// look 23x faster at a comparison the two were not both doing.
    ///
    /// The file's own prediction said that if this driver came out much faster,
    /// the benchmark was wrong before the driver was right. It did, and it was.
    /// </remarks>
    [Benchmark(Description = "FlaUI: read a property from a held element")]
    public string ReadThroughFlaUi() => _flauiElement.Name;
}
