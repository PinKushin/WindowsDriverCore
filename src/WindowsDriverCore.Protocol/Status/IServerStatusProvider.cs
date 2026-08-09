using WindowsDriverCore.Protocol.Responses;

namespace WindowsDriverCore.Protocol.Status;

/// <summary>
/// Supplies the body of <c>GET /status</c>.
/// </summary>
/// <remarks>
/// An interface rather than a static helper so a test can substitute it. The
/// previous implementation read the assembly and the OS inline in the route
/// handler, which meant the only way to test the route was to accept whatever
/// the host machine reported.
/// </remarks>
public interface IServerStatusProvider
{
    /// <summary>Describes this driver build and the machine it runs on.</summary>
    /// <returns>The status body, which carries no response envelope.</returns>
    ServerStatus GetStatus();
}
