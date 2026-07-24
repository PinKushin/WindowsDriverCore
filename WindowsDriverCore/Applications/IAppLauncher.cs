namespace WindowsDriverCore.Applications;

public interface IAppLauncher
{
    int Launch(string appPath, string? arguments = null, string? workingDir = null);
    void Close(int processId);
    IReadOnlyList<int> GetAllTrackedProcessIds();
}
