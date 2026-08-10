using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WindowsDriverCore.Host.CommandLine;
using Interop.UIAutomationClient;
using WindowsDriverCore.Automation;
using WindowsDriverCore.Automation.Uia;
using WindowsDriverCore.Platform.Applications;
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

        ServerAddress address = ServerAddress.Parse(args);
        WebApplication app = Build(args, address);
        app.Run();
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

        builder.Services.AddSingleton<IServerStatusProvider, ServerStatusProvider>();
        builder.Services.AddSingleton<ISessionStore, SessionStore>();
        builder.Services.AddSingleton<SessionFactory>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MainWindowWaiter>();
        builder.Services.AddSingleton<IWindowLocator, WindowLocator>();
        builder.Services.AddSingleton<IApplicationLauncher, ApplicationLauncher>();
        builder.Services.AddSingleton<IApplicationTerminator, ApplicationTerminator>();
        builder.Services.AddSingleton<IUIAutomation>(_ => new CUIAutomationClass());
        builder.Services.AddSingleton<IElementFinder, UiaElementFinder>();
        // The resolver is layered: UiaElementResolver walks the tree, and
        // CachingElementResolver keeps the elements it finds so a second command
        // on the same element does not walk again. Measured at 19.4 ms against
        // 0.45 ms for a property read. One instance serves both the resolver
        // contract and the cache-release contract, so DELETE /session releases
        // the handles this exact object is holding.
        builder.Services.AddSingleton<UiaElementResolver>();
        builder.Services.AddSingleton(provider =>
            new CachingElementResolver(provider.GetRequiredService<UiaElementResolver>()));
        builder.Services.AddSingleton<IElementResolver>(
            provider => provider.GetRequiredService<CachingElementResolver>());
        builder.Services.AddSingleton<IElementHandleCache>(
            provider => provider.GetRequiredService<CachingElementResolver>());
        builder.Services.AddSingleton<IElementInspector, UiaElementInspector>();
        builder.Services.AddSingleton<IElementInteractor, UiaElementInteractor>();
        builder.Services.AddSingleton<IElementRegistry, ElementRegistry>();

        WebApplication app = builder.Build();

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
