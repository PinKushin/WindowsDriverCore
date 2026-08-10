using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// The real server, as its own process, on its own port.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not WebApplicationFactory.</b> That hosts the pipeline in the test
/// process: no executable, no socket, no console, and no argument parsing. A
/// server that fails to start, binds the wrong address, or crashes on launch
/// passes every in-process test.
/// </para>
/// <para>
/// <b>A free port, never the default.</b> 4723 is WinAppDriver's and the
/// developer's Appium Android suite uses it; binding it in a test would fight
/// whatever is already there and fail for a reason that has nothing to do with
/// the test. The port is asked for from the OS, so parallel runs cannot collide
/// either.
/// </para>
/// </remarks>
internal sealed class DriverServer : IDisposable
{
    private readonly Process _process;

    private DriverServer(Process process, HttpClient client, int port)
    {
        _process = process;
        Client = client;
        Port = port;
    }

    /// <summary>Talks to the running server.</summary>
    public HttpClient Client { get; }

    /// <summary>The port it bound.</summary>
    public int Port { get; }

    /// <summary>Starts the server, or returns null if it has not been built.</summary>
    public static DriverServer? Start()
    {
        string? executable = FindExecutable();
        if (executable is null)
        {
            return null;
        }

        int port = FreePort();

        Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable, port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                UseShellExecute = true,          // its own console window, so the run is visible
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            },
        };

        process.Start();

        HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // Wait on the condition — the socket answering — rather than on a guess
        // about how long a cold start takes.
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using HttpResponseMessage status = client
                    .GetAsync(new Uri("/status", UriKind.Relative)).GetAwaiter().GetResult();

                if (status.StatusCode == HttpStatusCode.OK)
                {
                    return new DriverServer(process, client, port);
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            Thread.Sleep(100);
        }

        client.Dispose();
        Kill(process);
        return null;
    }

    /// <summary>Stops the server.</summary>
    public void Dispose()
    {
        Client.Dispose();
        Kill(_process);
        _process.Dispose();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>A port the OS says is free.</summary>
    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WindowsDriverCore.slnx")))
        {
            directory = directory.Parent;
        }

        string? host = directory is null ? null : Path.Combine(directory.FullName, "src", "WindowsDriverCore.Host");

        return host is not null && Directory.Exists(host)
            ? Directory.EnumerateFiles(host, "WindowsDriverCore.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }
}
