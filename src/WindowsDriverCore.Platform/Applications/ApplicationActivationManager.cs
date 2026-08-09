using System.Runtime.InteropServices;

namespace WindowsDriverCore.Platform.Applications;

/// <summary>Options for <see cref="IApplicationActivationManager.ActivateApplication"/>.</summary>
[Flags]
internal enum ActivateOptions
{
    /// <summary>No options.</summary>
    None = 0x00000000,

    /// <summary>Activate for debugging.</summary>
    DesignMode = 0x00000001,

    /// <summary>Suppress error dialogs on failure.</summary>
    NoErrorUI = 0x00000002,

    /// <summary>Suppress the splash screen.</summary>
    NoSplashScreen = 0x00000004,
}

/// <summary>
/// Activates packaged applications by AUMID.
/// </summary>
/// <remarks>
/// The supported way to start a packaged app from a desktop process.
/// <c>Process.Start</c> cannot: the executable inside <c>WindowsApps</c> is ACL'd
/// against direct launch, and <c>shell:AppsFolder\{aumid}</c> works but returns
/// the shell's process rather than the app's, leaving nothing to track.
/// </remarks>
[ComImport]
[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    /// <summary>Activates an application.</summary>
    /// <param name="appUserModelId">The AUMID, e.g. <c>Package_hash!App</c>.</param>
    /// <param name="arguments">Launch arguments, or null.</param>
    /// <param name="options">Activation options.</param>
    /// <param name="processId">
    /// Receives the activated process id. Note this can be the broker rather than
    /// the process that ends up owning the window.
    /// </param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        ActivateOptions options,
        out uint processId);

    /// <summary>Activates for a file association. Unused; present to keep the vtable aligned.</summary>
    /// <param name="appUserModelId">The AUMID.</param>
    /// <param name="itemArray">The files.</param>
    /// <param name="verb">The verb.</param>
    /// <param name="processId">Receives the process id.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int ActivateForFile(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        nint itemArray,
        [MarshalAs(UnmanagedType.LPWStr)] string verb,
        out uint processId);

    /// <summary>Activates for a protocol. Unused; present to keep the vtable aligned.</summary>
    /// <param name="appUserModelId">The AUMID.</param>
    /// <param name="itemArray">The protocol data.</param>
    /// <param name="processId">Receives the process id.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int ActivateForProtocol(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        nint itemArray,
        out uint processId);
}

/// <summary>
/// The concrete activation manager.
/// </summary>
/// <remarks>
/// The two unused methods above are not optional. A COM interface is a vtable in
/// declaration order, so omitting them would silently bind
/// <c>ActivateApplication</c> to the wrong slot on any later call.
/// </remarks>
[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal class ApplicationActivationManager
{
}
