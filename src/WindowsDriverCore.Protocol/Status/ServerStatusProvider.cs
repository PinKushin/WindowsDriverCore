using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using WindowsDriverCore.Protocol.Responses;

namespace WindowsDriverCore.Protocol.Status;

/// <summary>
/// Reports this driver build and the host it is running on.
/// </summary>
public sealed class ServerStatusProvider : IServerStatusProvider
{
    /// <inheritdoc />
    public ServerStatus GetStatus()
    {
        AssemblyName assembly = typeof(ServerStatusProvider).Assembly.GetName();
        Version version = assembly.Version ?? new Version(0, 0, 0, 0);

        return new ServerStatus(
            new BuildInfo(
                Version: version.ToString(3),
                Revision: version.Revision.ToString(CultureInfo.InvariantCulture),
                Time: BuildTimestamp()),
            new OperatingSystemInfo(
                // WinAppDriver reports "amd64", not .NET's "X64". Clients that
                // branch on architecture read this string, so it matches.
                Arch: RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => "amd64",
                    Architecture.X86 => "x86",
                    Architecture.Arm64 => "arm64",
                    _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                },
                Name: "windows",
                Version: Environment.OSVersion.Version.ToString()));
    }

    /// <summary>
    /// The build timestamp, taken from the assembly file rather than the current
    /// time. Reporting <c>DateTime.UtcNow</c> here — which the previous
    /// implementation did — makes the field change on every request and describes
    /// the request rather than the build.
    /// </summary>
    private static string BuildTimestamp()
    {
        string location = typeof(ServerStatusProvider).Assembly.Location;
        DateTime written = string.IsNullOrEmpty(location)
            ? DateTime.UnixEpoch
            : System.IO.File.GetLastWriteTimeUtc(location);

        return written.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture);
    }
}
