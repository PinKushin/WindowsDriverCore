using WindowsDriverCore.ErrorHandling;
using WindowsDriverCore.Messages;
using WindowsDriverCore.Sessions;

namespace WindowsDriverCore.Routes;

public static class SessionRoutes
{
    public static void MapSessionRoutes(this WebApplication app)
    {
        app.MapPost("/session", (SessionRequest request, ISessionStore store) =>
        {
            throw new NotImplementedException();
        });

        app.MapDelete("/session/{sessionId}", (string sessionId, ISessionStore store) =>
        {
            throw new NotImplementedException();
        });
    }
}
