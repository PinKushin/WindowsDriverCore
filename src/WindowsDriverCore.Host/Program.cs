using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WindowsDriverCore.Host.CommandLine;
using WindowsDriverCore.Protocol.Routing;
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

        WebApplication app = builder.Build();

        if (address.BasePath is not null)
        {
            // One mount, configured at startup — the same shape WinAppDriver has.
            // Serving both the root and the base path would be a behaviour it
            // does not have.
            app.UsePathBase(address.BasePath);
        }

        app.MapJsonWireProtocol();

        return app;
    }
}
