using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsDriverCore.Windows;

namespace WindowsDriverCore.Applications;

public class AppLauncher : IAppLauncher
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();
    private readonly ConcurrentBag<int> _allTrackedPids = new();

    public int Launch(string appPath, string? arguments = null, string? workingDir = null)
    {
        if (string.IsNullOrWhiteSpace(appPath))
            throw new FileNotFoundException($"Application not found: {appPath}");

        if (IsDesktopAppId(appPath))
            return 0;

        if (IsUwpAumid(appPath))
            return LaunchUwpApp(appPath, arguments);

        return LaunchClassicApp(appPath, arguments, workingDir);
    }

    public void Close(int processId)
    {
        if (processId == 0)
            return;

        // Dispose tracked process object if we have it
        if (_processes.TryRemove(processId, out var trackedProcess))
        {
            try { trackedProcess.Dispose(); } catch { }
        }

        var processName = GetProcessNameById(processId);

        // Explorer is the Windows shell — never kill it, just close the window
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            CloseExplorerWindows(processId);
            return;
        }

        // For multi-process browsers like Edge, kill ALL processes by name
        if (processName != null)
        {
            KillAllProcessesByName(processName);
            return;
        }

        // Step 1: Find all top-level windows owned by this process
        var ownedWindows = FindOwnedWindows(processId);

        // Step 2: Graceful close — send WM_CLOSE to all windows
        foreach (var hWnd in ownedWindows)
        {
            try { Win32.PostMessage(hWnd, Win32.WM_CLOSE, 0, 0); } catch { }
        }
        WaitForProcessExit(processId, 1000);

        // Step 3: Force kill using taskkill /F /T (OS-native tree kill — most reliable)
        if (!IsProcessDead(processId))
        {
            RunTaskKill(processId);
            WaitForProcessExit(processId, 2000);
        }

        // Step 4: If still alive, kill child processes bottom-up then parent
        if (!IsProcessDead(processId))
        {
            KillProcessTreeBottomUp(processId);
            WaitForProcessExit(processId, 1000);
        }

        // Step 5: Final resort — Process.Kill(entireProcessTree: true)
        if (!IsProcessDead(processId))
        {
            try
            {
                using var proc = Process.GetProcessById(processId);
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    private static void CloseExplorerWindows(int processId)
    {
        // Explorer windows are owned by the shell process, not the launched PID.
        // Find ALL top-level windows with Explorer-related classes and close them.
        var explorerWindows = new List<IntPtr>();

        Win32.EnumWindows((hWnd, _) =>
        {
            var className = GetWindowClassName(hWnd);
            // CabinetWClass = File Explorer, ExploreWClass = Explorer bar
            if (className is "CabinetWClass" or "ExploreWClass")
                explorerWindows.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var hWnd in explorerWindows)
        {
            try { Win32.PostMessage(hWnd, Win32.WM_CLOSE, 0, 0); } catch { }
        }
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        Win32.GetClassName(hWnd, sb, 256);
        return sb.ToString();
    }

    public IReadOnlyList<int> GetAllTrackedProcessIds()
    {
        return _allTrackedPids.ToList();
    }

    public void CloseWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !Win32.IsWindow(windowHandle))
            return;

        Win32.PostMessage(windowHandle, Win32.WM_CLOSE, 0, 0);
    }

    private static bool IsUwpAumid(string appId)
    {
        return appId.Contains('!') && !Path.IsPathRooted(appId);
    }

    internal static bool IsDesktopAppId(string appId)
    {
        return string.Equals(appId, "Root", StringComparison.OrdinalIgnoreCase);
    }

    private int LaunchUwpApp(string aumid, string? arguments)
    {
        bool isEdge = aumid.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase) &&
                      !aumid.Contains("Stable", StringComparison.OrdinalIgnoreCase);

        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            manager.ActivateApplication(aumid, arguments, ActivateOptions.None, out var processId);
            var pid = (int)processId;

            // COM returns PID=0 for Edge — it doesn't actually launch, fallback to msedge.exe
            if (pid == 0 && isEdge)
                return LaunchChromiumEdge(arguments);

            if (pid == 0)
                throw new InvalidOperationException("Value does not fall within the expected range.");

            _allTrackedPids.Add(pid);
            return pid;
        }
        catch (Exception)
        {
            if (isEdge)
                return LaunchChromiumEdge(arguments);
            throw new InvalidOperationException("Value does not fall within the expected range.");
        }
    }

    private int LaunchChromiumEdge(string? arguments)
    {
        string[] edgePaths = [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        ];

        var edgePath = edgePaths.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Value does not fall within the expected range.");

        var psi = new ProcessStartInfo
        {
            FileName = edgePath,
            UseShellExecute = false
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        try
        {
            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Value does not fall within the expected range.");
            _processes[proc.Id] = proc;
            _allTrackedPids.Add(proc.Id);
            return proc.Id;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Value does not fall within the expected range.", ex);
        }
    }

    private int LaunchClassicApp(string appPath, string? arguments, string? workingDir)
    {
        // Validate working directory first, before any launch attempt
        if (!string.IsNullOrEmpty(workingDir) && !Directory.Exists(workingDir))
            throw new InvalidOperationException("The directory name is invalid");

        var resolvedPath = ResolveAppPath(appPath);

        // Detect packaged UWP apps: either direct WindowsApps path or redirect stub in System32
        var isWindowsAppsPath = resolvedPath.Contains(@"WindowsApps\", StringComparison.OrdinalIgnoreCase);
        var aumid = isWindowsAppsPath
            ? GetAumidFromPath(resolvedPath)
            : FindAumidForExe(Path.GetFileName(resolvedPath));

        if (!string.IsNullOrEmpty(aumid))
        {
            if (aumid.Contains('!'))
            {
                try
                {
                    var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                    manager.ActivateApplication(aumid, arguments, ActivateOptions.None, out var pid);
                    var processId = (int)pid;

                    if (processId != 0)
                    {
                        _allTrackedPids.Add(processId);
                        return processId;
                    }
                }
                catch (COMException)
                {
                    // Fall through to shell:AppsFolder
                }
            }
            return LaunchViaShellAppsFolder(aumid, exeName: Path.GetFileNameWithoutExtension(resolvedPath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedPath,
            UseShellExecute = false
        };

        if (!string.IsNullOrEmpty(arguments))
            startInfo.Arguments = arguments;

        if (!string.IsNullOrEmpty(workingDir))
        {
            startInfo.WorkingDirectory = workingDir;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {appPath}");

        _processes[process.Id] = process;
        _allTrackedPids.Add(process.Id);
        return process.Id;
    }

    private static string GetAumidFromPath(string resolvedPath)
    {
        try
        {
            // Extract package family name from WindowsApps path
            // e.g. C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2605.34.0_x64__8wekyb3d8bbwe\...
            var windowsAppsIdx = resolvedPath.IndexOf(@"WindowsApps\", StringComparison.OrdinalIgnoreCase);
            if (windowsAppsIdx < 0) return "";

            var afterWindowsApps = resolvedPath[(windowsAppsIdx + @"WindowsApps\".Length)..];
            var folderName = afterWindowsApps.Split('\\')[0];

            // Folder format: {PackageName}_{Version}_{Architecture}__{PublisherId}
            // Family name: {PackageName}_{PublisherId}
            var lastDdu = folderName.LastIndexOf("__");
            if (lastDdu < 0) return "";

            var publisherId = folderName[(lastDdu + 2)..];
            var prefix = folderName[..lastDdu];

            // Find the first underscore after the package name (version starts with digit)
            var parts = prefix.Split('_');
            if (parts.Length < 2) return "";

            var packageName = parts[0];
            return $"{packageName}_{publisherId}";
        }
        catch
        {
            return "";
        }
    }

    private int LaunchViaShellAppsFolder(string aumid, string exeName)
    {
        var beforePids = new HashSet<int>(Process.GetProcessesByName(exeName).Select(p => p.Id));

        var startInfo = new ProcessStartInfo
        {
            FileName = $"shell:AppsFolder\\{aumid}",
            UseShellExecute = true
        };

        try
        {
            var proc = Process.Start(startInfo);
            proc?.Dispose();
        }
        catch
        {
            // Fallback: try via explorer
            var fallback = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{aumid}",
                UseShellExecute = false
            };
            var ep = Process.Start(fallback);
            ep?.Dispose();
        }

        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(100);
            var newProcs = Process.GetProcessesByName(exeName)
                .Where(p => !beforePids.Contains(p.Id) && !p.HasExited)
                .ToList();

            if (newProcs.Count > 0)
            {
                var proc = newProcs[0];
                _processes[proc.Id] = proc;
                _allTrackedPids.Add(proc.Id);
                return proc.Id;
            }
        }

        throw new InvalidOperationException($"Failed to start packaged app: {aumid}");
    }

    private static string FindAumidForExe(string exeName)
    {
        try
        {
            var exeFile = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
            var exeBase = Path.GetFileNameWithoutExtension(exeFile);

            // Use PowerShell Get-AppxPackage to find packaged version of this exe
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command (Get-AppxPackage -Name '*{exeBase}*' | Select-Object -First 1).PackageFamilyName",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            var proc = Process.Start(startInfo);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                proc.Dispose();

                if (!string.IsNullOrEmpty(output))
                {
                    // shell:AppsFolder needs the !App suffix (full AUMID format)
                    if (!output.Contains('!'))
                        output = output + "!App";
                    return output;
                }
            }
        }
        catch { }

        return "";
    }

    private static string ResolveAppPath(string appPath)
    {
        if (File.Exists(appPath))
            return appPath;

        // Fallback: on Win11+, some system executables moved from System32
        var exeName = Path.GetFileName(appPath);
        if (exeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var fallback = Path.Combine(winDir, exeName);
            if (File.Exists(fallback))
                return fallback;
        }

        var exeBase = Path.GetFileNameWithoutExtension(appPath);
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, exeName + ".exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var exeWithExt = Path.GetFileName(appPath);
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, exeWithExt);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("The system cannot find the file specified");
    }

    // --- Process kill helpers ---

    private static string? GetProcessNameById(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            return proc.ProcessName;
        }
        catch { return null; }
    }

    private static void KillAllProcessesByName(string processName)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            foreach (var proc in procs)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }

        // Fallback: taskkill by name
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/F /IM {processName}.exe /T",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            proc?.Dispose();
        }
        catch { }
    }

    private static List<IntPtr> FindOwnedWindows(int processId)
    {
        var windows = new List<IntPtr>();
        try
        {
            Win32.EnumWindows((hWnd, _) =>
            {
                Win32.GetWindowThreadProcessId(hWnd, out var winPid);
                if ((int)winPid == processId && Win32.IsWindow(hWnd))
                    windows.Add(hWnd);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return windows;
    }

    private static void RunTaskKill(int processId)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/F /T /PID {processId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            proc?.Dispose();
        }
        catch { }
    }

    private static void KillProcessTreeBottomUp(int processId)
    {
        try
        {
            var childIds = Win32.GetChildProcessIds(processId);
            foreach (var childId in childIds)
                KillProcessTreeBottomUp(childId);

            using var proc = Process.GetProcessById(processId);
            if (!proc.HasExited)
                proc.Kill();
        }
        catch { }
    }

    private static void WaitForProcessExit(int processId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (IsProcessDead(processId))
                return;
            Thread.Sleep(100);
        }
    }

    private static bool IsProcessDead(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            return proc.HasExited;
        }
        catch
        {
            return true;
        }
    }

    // --- COM Interop for ApplicationActivationManager ---

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ApplicationActivationManager
    {
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);
    }

    [Flags]
    internal enum ActivateOptions
    {
        None = 0x00000000,
        DesignMode = 0x00000001,
        NoErrorUI = 0x00000002,
        NoSplashScreen = 0x00000004,
    }
}
