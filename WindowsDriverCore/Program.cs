using System.Runtime.InteropServices;

namespace WindowsDriverCore
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            app.MapGet("/", () => "Hello World!");

            app.MapGet("/status/", () =>
            {
                var osArch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
                var osVersion = Environment.OSVersion.Version.ToString();

                return Results.Json(new
                {
                    build = new
                    {
                        version = "1.0.0",
                        revision = "0",
                        time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    },
                    os = new
                    {
                        arch = osArch,
                        name = "windows",
                        version = osVersion
                    }
                });
            });

            app.Run();
        }
    }
}
