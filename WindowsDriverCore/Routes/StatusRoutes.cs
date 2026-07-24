using System.Reflection;
using System.Runtime.InteropServices;
using WindowsDriverCore.Messages;

namespace WindowsDriverCore.Routes;

public static class StatusRoutes
{
    public static void MapStatusRoutes(this WebApplication app)
    {
        app.MapGet("/status/", () =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var revision = assembly.GetName().Version?.Revision.ToString() ?? "0";

            var info = new StatusInfo(
                new BuildInfo(version, revision, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                new OsInfo(
                    RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                    "windows",
                    Environment.OSVersion.Version.ToString()
                )
            );
            return Results.Json(info);
        });
    }
}
