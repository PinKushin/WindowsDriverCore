namespace WindowsDriverCore.Applications;

public interface IAppLauncher
{
    int Launch(string appPath);
    void Close(int processId);
}
