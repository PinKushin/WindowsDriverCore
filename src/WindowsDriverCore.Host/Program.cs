using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WindowsDriverCore.Host.CommandLine;
using WindowsDriverCore.Host.Diagnostics;
using Interop.UIAutomationClient;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Diagnostics;
using WindowsDriverCore.Automation.Diagnostics;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
using WindowsDriverCore.Platform.Diagnostics;
using WindowsDriverCore.Platform.Windows;
using WindowsDriverCore.Protocol.Routing;
using WindowsDriverCore.Protocol.Sessions;
using WindowsDriverCore.Protocol.Status;

namespace WindowsDriverCore.Host;

/// <summary>
/// Entry point and composition root.
/// </summary>
/// <remarks>
/// Not static, and not a top-level statements file, so that
/// <c>WebApplicationFactory&lt;Program&gt;</c> can boot the real pipeline
/// in-memory. Protocol tests then exercise the same routing and serialization
/// the shipped server uses, rather than a parallel arrangement that can drift.
/// </remarks>
public partial class Program
{
    /// <summary>
    /// Not instantiable. The type exists as a type argument for
    /// <c>WebApplicationFactory</c>, which is why it cannot simply be static.
    /// </summary>
    protected Program()
    {
    }

    /// <summary>Runs the driver.</summary>
    /// <param name="args">
    /// WinAppDriver-compatible forms: none, <c>[port]</c>, <c>[ip] [port]</c>, or
    /// <c>[ip] [port]/base/path</c>. <c>*</c> as the address binds all interfaces.
    /// </param>
    public static void Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        RegisterCrashReporting();

        ServerAddress address = ServerAddress.Parse(args);
        WebApplication app = Build(args, address);
        app.Run();
    }

    /// <summary>
    /// Writes a local report when the process crashes, and points at it on
    /// the way down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>First thing in <see cref="Main"/>, before anything else.</b> A crash
    /// during startup — construction of the UI Automation root object, capability
    /// parsing, anything before <c>app.Run()</c> — has to be caught too, and
    /// registering this after those would leave exactly that window uncovered.
    /// </para>
    /// <para>
    /// <b>What this does NOT catch, and it matters.</b> ASP.NET Core's own
    /// pipeline catches an exception thrown while handling a request and turns
    /// it into a 500 response — that exception never reaches
    /// <c>AppDomain.UnhandledException</c>, because something up the call stack
    /// already handled it. This hook only fires for what nothing else caught:
    /// a background thread, a fire-and-forget <c>Task</c>, startup before the
    /// pipeline exists. A request handler that throws is a protocol bug to fix,
    /// not a crash to report.
    /// </para>
    /// </remarks>
    private static void RegisterCrashReporting()
    {
        CrashDumpWriter writer = new(CrashDumpWriter.DefaultDirectory, TimeProvider.System);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // ExceptionObject is object, not Exception - the CLR allows a
            // non-Exception throw from unverifiable code, rare as that is. A
            // report is still written rather than silently dropped: "unhandled
            // non-Exception" is a fact worth having on disk too.
            Exception exception = e.ExceptionObject as Exception
                ?? new InvalidOperationException(
                    $"Unhandled non-Exception object thrown: {e.ExceptionObject}");

            string path = writer.Write(exception, e.IsTerminating);
            Console.Error.WriteLine($"WindowsDriverCore crashed. Report: {path}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            string path = writer.Write(e.Exception, isTerminating: false);
            Console.Error.WriteLine($"An unobserved task exception was recorded: {path}");

            // Marked observed so the finalizer thread does not re-throw it and
            // take the process down anyway - the report has already been
            // written, and that second throw would just be a worse copy of it.
            e.SetObserved();
        };
    }

    /// <summary>
    /// Builds the application. Shared by <see cref="Main"/> and the test host so
    /// both exercise the same pipeline.
    /// </summary>
    /// <param name="args">Raw process arguments, for configuration binding.</param>
    /// <param name="address">The parsed listen address.</param>
    /// <returns>The configured application.</returns>
    internal static WebApplication Build(string[] args, ServerAddress address)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(address.ToListenUrl());

        // QUIET THE FRAMEWORK'S OWN CONSOLE LOGGER, or the transcript is
        // unreadable. WebApplication.CreateBuilder wires a console provider at
        // Information, and ASP.NET emits "Request starting", "Executing
        // endpoint", "Writing value of type ... as Json" and "Request finished"
        // for EVERY request. Measured 2026-08-11: one GET /status produced six
        // framework lines around one transcript line. WinAppDriver's console is
        // clean, and matching it is the whole reason the transcript defaults to
        // the console.
        //
        // Warning rather than ClearProviders: an unhandled exception is logged
        // through this pipeline at Error, and silencing that to tidy the output
        // would trade a real signal for a cosmetic one.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // One EventSource for the process, reached only through its interface.
        // Registered by concrete type as well so DI owns the single instance and
        // disposes it; resolving IRequestLog separately would construct a second.
        builder.Services.AddSingleton<DriverEventSource>();
        builder.Services.AddSingleton<IRequestLog>(
            provider => provider.GetRequiredService<DriverEventSource>());
        builder.Services.AddSingleton<IFindLog>(
            provider => provider.GetRequiredService<DriverEventSource>());
        builder.Services.AddSingleton<IInteractionLog>(
            provider => provider.GetRequiredService<DriverEventSource>());
        builder.Services.AddSingleton<ILaunchLog>(
            provider => provider.GetRequiredService<DriverEventSource>());
        builder.Services.AddSingleton<ITerminationLog>(
            provider => provider.GetRequiredService<DriverEventSource>());
        builder.Services.AddSingleton<IResolveLog>(
            provider => provider.GetRequiredService<DriverEventSource>());
        builder.Services.AddSingleton<IPageSourceLog>(
            provider => provider.GetRequiredService<DriverEventSource>());
        builder.Services.AddSingleton<IPointerLog>(
            provider => provider.GetRequiredService<DriverEventSource>());

        // The transcript's destination and the consumer that fills it. Both are
        // DI singletons so the host disposes them: the listener unsubscribes and
        // a log file is closed rather than left locked.
        builder.Services.AddSingleton(
            _ => RequestLogDestination.Open(Environment.GetEnvironmentVariable));
        builder.Services.AddSingleton(provider => new TextRequestLogListener(
            provider.GetRequiredService<RequestLogDestination>().Writer,
            provider.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton<IServerStatusProvider, ServerStatusProvider>();
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        builder.Services.AddSingleton<SessionFactory>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MainWindowWaiter>();
        builder.Services.AddSingleton<IWindowLocator, WindowLocator>();
        // DECORATED, here and nowhere else. The real implementations stay
        // unaware of the transcript, their nineteen construction sites across the
        // test projects stay unchanged, and the wiring that adds the logging is
        // one line each in the composition root.
        builder.Services.AddSingleton<ApplicationLauncher>();
        builder.Services.AddSingleton<IApplicationLauncher>(provider =>
            new LoggingApplicationLauncher(
                provider.GetRequiredService<ApplicationLauncher>(),
                provider.GetRequiredService<ILaunchLog>()));
        builder.Services.AddSingleton<ApplicationTerminator>();
        builder.Services.AddSingleton<IApplicationTerminator>(provider =>
            new LoggingApplicationTerminator(
                provider.GetRequiredService<ApplicationTerminator>(),
                provider.GetRequiredService<ITerminationLog>()));
        builder.Services.AddSingleton<IPointerInput, SendInputPointer>();
        builder.Services.AddSingleton<ISyntheticPointer, SyntheticPointer>();
        builder.Services.AddSingleton<PointerActionRunner>();
        builder.Services.AddSingleton<IKeyboardInput, SendInputKeyboard>();
        builder.Services.AddSingleton<IUIAutomation>(_ => new CUIAutomationClass());
        builder.Services.AddSingleton<UiaElementFinder>();
        builder.Services.AddSingleton<IElementFinder>(provider =>
            new LoggingElementFinder(
                provider.GetRequiredService<UiaElementFinder>(),
                provider.GetRequiredService<IFindLog>()));
        // The resolver is layered: UiaElementResolver walks the tree, and
        // CachingElementResolver keeps the elements it finds so a second command
        // on the same element does not walk again. Measured at 19.4 ms against
        // 0.45 ms for a property read. One instance serves both the resolver
        // contract and the cache-release contract, so DELETE /session releases
        // the handles this exact object is holding.
        builder.Services.AddSingleton<UiaElementResolver>();
        builder.Services.AddSingleton(provider =>
            new CachingElementResolver(provider.GetRequiredService<UiaElementResolver>()));
        // Wrapped OUTSIDE the cache, so the recorded cost is what the caller
        // paid. Inside it would time only the misses and report the hits as
        // nothing at all, which is the opposite of the question being asked.
        builder.Services.AddSingleton<IElementResolver>(provider =>
            new LoggingElementResolver(
                provider.GetRequiredService<CachingElementResolver>(),
                provider.GetRequiredService<IResolveLog>()));
        builder.Services.AddSingleton<IElementHandleCache>(
            provider => provider.GetRequiredService<CachingElementResolver>());
        builder.Services.AddSingleton<IElementInspector, UiaElementInspector>();
        builder.Services.AddSingleton<UiaPageSource>();
        builder.Services.AddSingleton<IPageSourceReader>(provider =>
            new LoggingPageSourceReader(
                provider.GetRequiredService<UiaPageSource>(),
                provider.GetRequiredService<IPageSourceLog>()));
        builder.Services.AddSingleton<UiaElementInteractor>();
        builder.Services.AddSingleton<IElementInteractor>(provider =>
            new LoggingElementInteractor(
                provider.GetRequiredService<UiaElementInteractor>(),
                provider.GetRequiredService<IInteractionLog>()));
        builder.Services.AddSingleton<IElementRegistry, ElementRegistry>();

        WebApplication app = builder.Build();

        // RESOLVED EAGERLY, and it has to be. A DI singleton nobody asks for is
        // never constructed, and an EventListener that is never constructed never
        // subscribes — so the events would be published to an empty room and the
        // whole transcript would be silently missing.
        _ = app.Services.GetRequiredService<TextRequestLogListener>();

        // Said out loud, because a relative path resolves against the working
        // directory and that is not always where the person who set the variable
        // expected. Silence about a file is how a transcript gets written
        // somewhere nobody looks.
        // OUR OWN BANNER. Silencing the framework logger above also silenced
        // "Now listening on: ...", which is the one framework line that was worth
        // having - and which WinAppDriver prints too. Losing it while quieting
        // the noise around it would be a bad trade made by accident.
        Console.WriteLine($"WindowsDriverCore listening on {address.ToListenUrl()}");

        string? logPath = app.Services.GetRequiredService<RequestLogDestination>().Path;
        Console.WriteLine(logPath is null
            ? "Request transcript: this console"
            : $"Request transcript: {logPath}");

        // FIRST, so nothing is invisible to it — including the base-path gate's
        // 404, which never reaches routing. A transcript with a hole in it is
        // worse than none, because the hole leaves no trace.
        app.UseMiddleware<RequestLogMiddleware>();

        if (address.BasePath is not null)
        {
            // One mount, configured at startup — the same shape WinAppDriver has.
            //
            // Both lines are needed and neither is sufficient. UsePathBase strips
            // the prefix when a request carries it, but lets everything else
            // through untouched, so on its own the server answers BOTH
            // /wd/hub/status and bare /status. Measured against WinAppDriver
            // 1.2.2009.02003 started with "127.0.0.1 4728/wd/hub": the prefixed
            // path returns 200 and the bare path returns 404. The gate supplies
            // that rejection; UsePathBase then lets routes be declared once, at
            // the root.
            app.UseMiddleware<BasePathGate>(address.BasePath);
            app.UsePathBase(address.BasePath);
        }

        // Explicit, and load-bearing. WebApplication inserts UseRouting at the
        // FRONT of the pipeline when endpoints exist and it has not been called,
        // which would run routing before UsePathBase and leave the base path with
        // no effect. Calling it here suppresses the automatic one.
        //
        // The in-memory protocol tests could not catch this: they never request a
        // base path. It was found by running the real executable with
        // "127.0.0.1 4725/wd/hub" and watching /wd/hub/status 404 while bare
        // /status answered 200.
        app.UseRouting();

        app.MapJsonWireProtocol();

        return app;
    }
}
