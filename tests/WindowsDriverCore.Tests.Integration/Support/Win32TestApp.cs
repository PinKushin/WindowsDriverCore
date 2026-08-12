using System;
using System.IO;
using System.Linq;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>
/// The repository's pure Win32 subject.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="TestApp"/>, which is WPF, because "classic" on
/// Windows means Win32 and WPF is not that.</b> WPF puts one HwndSource window
/// up and renders its own content, so UI Automation sees WPF automation peers.
/// This one registers a window class and holds real EDIT, BUTTON and STATIC
/// children, which UIA reaches through the legacy MSAA bridge - a different
/// provider entirely.
/// </para>
/// <para>
/// It also exists to get the suite off Notepad. There is no Win32 Notepad on
/// Windows 11: the System32 entry is a shim to the packaged build, whose
/// session-restore modals kept landing on a shared desktop.
/// </para>
/// </remarks>
internal static class Win32TestApp
{
    private const string ExecutableName = "TestApp.exe";

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

            string app = System.IO.Path.Combine(directory.FullName, "TestApp");

            return !Directory.Exists(app)
                ? null
                : Directory.EnumerateFiles(app, ExecutableName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
        }
    }

    /// <summary>The process name to look for in teardown.</summary>
    public static string ProcessName => "TestApp";
}
