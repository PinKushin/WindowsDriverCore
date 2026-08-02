using System.Runtime.InteropServices;
using System.Text.Json;
using WindowsDriverCore.Applications;
using WindowsDriverCore.Automation;
using WindowsDriverCore.ErrorHandling;
using WindowsDriverCore.Messages;
using WindowsDriverCore.Routes;
using WindowsDriverCore.Sessions;
using WindowsDriverCore.Windows;

// P/Invoke for DPI awareness — must be called before any windows are created
[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(IntPtr value);

try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:4723");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton<ISessionStore, SessionStore>();
builder.Services.AddSingleton<IAppLauncher, AppLauncher>();
builder.Services.AddSingleton<IWindowFinder, WindowFinder>();
builder.Services.AddSingleton<ElementStore>();
builder.Services.AddSingleton<IElementFinder, ElementFinder>();
builder.Services.AddSingleton<IElementInteractor, ElementInteractor>();
builder.Services.AddHostedService<SessionCleanupService>();

var app = builder.Build();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var cleanup = app.Services.GetRequiredService<SessionCleanupService>();
    cleanup.KillAllTrackedProcesses();
});

app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    Console.WriteLine($"--> {context.Request.Method} {context.Request.Path}");
    await next();
    Console.WriteLine($"<-- {context.Response.StatusCode}");
});

app.UseExceptionHandler(error =>
{
    error.Run(async context =>
    {
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

        if (exception is WebDriverException webDriverEx)
        {
            context.Response.StatusCode = webDriverEx.HttpStatus;
            context.Response.ContentType = "application/json";
            var body = new ErrorResponse(new WebDriverError(webDriverEx.ErrorCode, webDriverEx.Message, ""));
            await context.Response.WriteAsJsonAsync(body);
        }
        else
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            var body = new ErrorResponse(new WebDriverError(ErrorType.UnknownError, exception?.Message ?? "Unknown error", ""));
            await context.Response.WriteAsJsonAsync(body);
        }
    });
});

app.MapGet("/", () => "Hello World!");

app.MapStatusRoutes();
app.MapSessionRoutes();
app.MapElementRoutes();

app.Run();
