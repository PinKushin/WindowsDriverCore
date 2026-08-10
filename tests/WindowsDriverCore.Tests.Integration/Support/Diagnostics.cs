using System.Runtime.InteropServices;

namespace WindowsDriverCore.Tests.Integration.Support;

/// <summary>Desktop state a UI test needs in order to explain itself.</summary>
/// <remarks>
/// DllImport rather than LibraryImport: the source generator emits unsafe code,
/// and turning unsafe on across the test project for two declarations is a worse
/// trade than declaring them the old way.
/// </remarks>
internal static class Diagnostics
{
    /// <summary>The window currently in the foreground.</summary>
    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();
}
