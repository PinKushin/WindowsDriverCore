using System.Diagnostics;
using WindowsDriverCore.Sessions;
using WindowsDriverCore.Windows;

namespace WindowsDriverCore.Applications;

public class SessionCleanupService : BackgroundService
{
    private readonly ISessionStore _sessions;
    private readonly IWindowFinder _windowFinder;
    private readonly IAppLauncher _launcher;
    private readonly ILogger<SessionCleanupService> _logger;

    public SessionCleanupService(
        ISessionStore sessions,
        IWindowFinder windowFinder,
        IAppLauncher launcher,
        ILogger<SessionCleanupService> logger)
    {
        _sessions = sessions;
        _windowFinder = windowFinder;
        _launcher = launcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                CleanupOrphanedProcesses();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup service error");
            }
        }
    }

    private void CleanupOrphanedProcesses()
    {
        // Clean up sessions with dead windows
        var allSessions = _sessions.GetAll();
        foreach (var session in allSessions)
        {
            if (session.ProcessId == 0)
                continue;

            if (session.MainWindowHandle == IntPtr.Zero)
                continue;

            if (_windowFinder.IsWindowValid(session.MainWindowHandle))
                continue;

            _logger.LogInformation(
                "Orphaned session {SessionId}: process {ProcessId} window gone, cleaning up",
                session.SessionId, session.ProcessId);

            try
            {
                _launcher.Close(session.ProcessId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill orphaned process {ProcessId}", session.ProcessId);
            }

            session.MainWindowHandle = IntPtr.Zero;
        }
    }

    public void KillAllTrackedProcesses()
    {
        var allSessions = _sessions.GetAll();
        foreach (var session in allSessions)
        {
            if (session.ProcessId == 0) continue;
            try { _launcher.Close(session.ProcessId); }
            catch { }
        }

        // Also kill any remaining tracked PIDs and their children
        foreach (var pid in _launcher.GetAllTrackedProcessIds())
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    try { proc.WaitForExit(2000); } catch { }
                }
            }
            catch { }
        }

        // Nuclear option: kill all lingering child processes of tracked PIDs
        foreach (var pid in _launcher.GetAllTrackedProcessIds())
        {
            try
            {
                foreach (var childId in Windows.Win32.GetChildProcessIds(pid))
                {
                    try
                    {
                        using var child = Process.GetProcessById(childId);
                        if (!child.HasExited)
                            child.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
