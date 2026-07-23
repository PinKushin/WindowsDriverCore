using WindowsDriverCore.Routes;
using WindowsDriverCore.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ISessionStore, SessionStore>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.MapStatusRoutes();
app.MapSessionRoutes();

app.Run();
