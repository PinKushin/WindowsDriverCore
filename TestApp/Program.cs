using System.Runtime.InteropServices;
using System.Text;
using TestApp;

namespace TestApp;

static class Program
{
    const string WindowClass = "TestAppWnd";
    const string EditClass = "Edit";
    const string ButtonClass = "Button";
    const string StaticClass = "Static";

    const int WM_CREATE = 0x0001;
    const int WM_DESTROY = 0x0002;
    const int WM_COMMAND = 0x0111;

    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const uint WS_CHILD = 0x40000000;
    const uint WS_VISIBLE = 0x10000000;
    const uint WS_BORDER = 0x00800000;
    const uint CS_HREDRAW = 0x0002;
    const uint CS_VREDRAW = 0x0001;
    const int COLOR_WINDOW = 5;
    const int IDC_ARROW = 32512;
    const int SW_SHOW = 5;
    const int CW_USEDEFAULT = unchecked((int)0x80000000);

    const int IDC_STATIC = 1001;
    const int IDC_EDIT = 1002;
    const int IDC_BUTTON = 1003;
    const int IDC_LABEL = 1004;

    const uint ES_AUTOHSCROLL = 0x0080;
    const uint BS_PUSHBUTTON = 0x00000000;

    static readonly Native.WndProcDelegate _wndProc = WndProc;

    static void Main()
    {
        var hInstance = Native.GetModuleHandleW(null);

        var wc = new Native.WNDCLASSEXW();
        wc.cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>();
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
        wc.hInstance = hInstance;
        wc.lpszClassName = WindowClass;
        wc.hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW);
        wc.hbrBackground = (IntPtr)(COLOR_WINDOW + 1);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        Native.RegisterClassExW(ref wc);

        IntPtr hWnd = Native.CreateWindowExW(
            0, WindowClass, "WindowsDriverCore Test App",
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, 500, 350,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        Native.ShowWindow(hWnd, SW_SHOW);
        Native.UpdateWindow(hWnd);

        Native.MSG msg;
        while (Native.GetMessageW(out msg, IntPtr.Zero, 0, 0))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CREATE:
            {
                var hInst = Native.GetModuleHandleW(null);

                Native.CreateWindowExW(0, StaticClass, "Status: Ready",
                    WS_CHILD | WS_VISIBLE,
                    10, 10, 460, 20,
                    hWnd, (IntPtr)IDC_STATIC, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, EditClass, "",
                    WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL,
                    10, 40, 460, 25,
                    hWnd, (IntPtr)IDC_EDIT, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, ButtonClass, "Click Me",
                    WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                    10, 80, 120, 30,
                    hWnd, (IntPtr)IDC_BUTTON, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, StaticClass, "Label for testing",
                    WS_CHILD | WS_VISIBLE,
                    10, 130, 460, 20,
                    hWnd, (IntPtr)IDC_LABEL, hInst, IntPtr.Zero);
                break;
            }
            case WM_COMMAND:
            {
                if ((wParam.ToInt64() & 0xFFFF) == IDC_BUTTON)
                {
                    var hEdit = Native.GetDlgItem(hWnd, IDC_EDIT);
                    var len = Native.GetWindowTextLengthW(hEdit) + 1;
                    var sb = new StringBuilder(len);
                    Native.GetWindowTextW(hEdit, sb, len);
                    var text = sb.ToString();

                    var hStatus = Native.GetDlgItem(hWnd, IDC_STATIC);
                    Native.SetWindowTextW(hStatus, $"Clicked! Text: {text}");
                }
                break;
            }
            case WM_DESTROY:
                Native.PostQuitMessage(0);
                break;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
