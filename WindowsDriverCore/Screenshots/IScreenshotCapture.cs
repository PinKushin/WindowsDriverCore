namespace WindowsDriverCore.Screenshots;

public interface IScreenshotCapture
{
    string CaptureBase64(IntPtr windowHandle);
}
