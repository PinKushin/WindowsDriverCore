using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
[SimpleJob(warmupCount: 3, iterationCount: 10)]
[SuppressMessage(
    "Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification =
        "BenchmarkDotNet owns this type's lifecycle and guarantees [GlobalCleanup] runs, " +
        "which is where the disposable fields are released. Implementing IDisposable here " +
        "would add a second disposal path that BenchmarkDotNet never calls.")]
public class FindBenchmarks
{
    private const string CalculatorAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    private UiaElementFinder _finder = null!;
    private CachingElementResolver _cachingResolver = null!;
    private UiaElementInspector _inspector = null!;
    private nint _window;
    private string _elementId = null!;

    private UIA3Automation _flaui = null!;
    private Window _flauiWindow = null!;
    private int _processId;

    [GlobalSetup]
    public void Setup()
    {
        CUIAutomationClass automation = new();
        _finder = new UiaElementFinder(automation);
        _cachingResolver = new CachingElementResolver(new UiaElementResolver(automation));
        _inspector = new UiaElementInspector(automation, _cachingResolver);

        LaunchResult launched = new ApplicationLauncher(
            new MainWindowWaiter(TimeProvider.System), new WindowLocator())
            .Launch(new ApplicationTarget(CalculatorAumid, null, null));

        if (launched.Application is null)
        {
            throw new InvalidOperationException(
                $"Calculator is required for this benchmark: {launched.FailureMessage}");
        }

        _window = launched.Application.WindowHandle;
        _processId = launched.Application.ProcessId;

        FindResult found = _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button");
        if (found.ElementIds.Count == 0)
        {
            throw new InvalidOperationException("num5Button was not found; the app is not ready.");
        }

        _elementId = found.ElementIds[0];

        // FlaUI attaches to the same window, so both subjects walk the same tree.
        _flaui = new UIA3Automation();
        _flauiWindow = _flaui.GetDesktop()
            .FindAllChildren()
            .Select(element => element.AsWindow())
            .First(window => window.Properties.ProcessId.ValueOrDefault == _processId);
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

    /// <summary>Finding one element by automation id, through this driver.</summary>
    [Benchmark(Baseline = true, Description = "ours: find by automation id")]
    public int FindThroughThisDriver() =>
        _finder.FindAll(_window, LocatorKind.AutomationId, "num5Button").ElementIds.Count;

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

    /// <summary>The same read through FlaUI, holding the element.</summary>
    [Benchmark(Description = "FlaUI: read a property from a held element")]
    public string? ReadThroughFlaUi() =>
        _flauiWindow.FindFirstDescendant(
            condition => condition.ByAutomationId("num5Button"))?.Name;
}
