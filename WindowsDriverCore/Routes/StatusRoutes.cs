using System.Runtime.InteropServices;
using WindowsDriverCore.Messages;

namespace WindowsDriverCore.Routes;

public static class StatusRoutes
{
    public static void MapStatusRoutes(this WebApplication app)
    {
        app.MapGet("/status/", () =>
        {
            var info = new StatusInfo(
                new BuildInfo("1.0.0", "0", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")),
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
