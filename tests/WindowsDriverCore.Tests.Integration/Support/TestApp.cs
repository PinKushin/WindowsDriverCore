using System;
using System.IO;
using System.Linq;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// Locates the purpose-built WPF test subject.
/// </summary>
/// <remarks>
/// Found by walking up to the solution file rather than by a relative path from
/// the test binary, because the depth of that path differs between a
/// <c>dotnet test</c> run and the IDE's runner.
/// </remarks>
internal static class TestApp
{
    private const string ExecutableName = "WindowsDriverCore.TestApp.Wpf.exe";

    /// <summary>The built executable, or null if it has not been built.</summary>
    public static string? Path
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null &&
                   !File.Exists(System.IO.Path.Combine(directory.FullName, "WindowsDriverCore.slnx")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                return null;
            }

            string apps = System.IO.Path.Combine(directory.FullName, "apps");

            return !Directory.Exists(apps)
                ? null
                : Directory.EnumerateFiles(apps, ExecutableName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
        }
    }

    /// <summary>The process name to kill in teardown.</summary>
    public static string ProcessName => "WindowsDriverCore.TestApp.Wpf";
}
