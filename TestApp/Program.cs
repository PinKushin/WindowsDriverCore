using System.Runtime.InteropServices;
using System.Text;

namespace TestApp;

/// <summary>
/// A pure Win32 subject for the integration suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Native controls, not a framework's rendering of them.</b> The window is a
/// registered window class holding real EDIT, BUTTON and STATIC children, so UI
/// Automation reaches them through the legacy MSAA bridge — a different provider
/// from the one WPF or WinUI expose. The WPF subject cannot cover that path, and
/// "classic" on Windows means Win32.
/// </para>
/// <para>
/// <b>It exists to replace Notepad.</b> There is no Win32 Notepad on Windows 11:
/// the System32 entry is a shim to the packaged build, which restores its
/// session after an abnormal exit and reopens with a modal on a desktop several
/// suites share. Two behaviours were only being tested through it, and both are
/// provided here instead.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Single-instance, multi-window.</b> "New Window" opens a second top-level
/// window in the SAME process, so a process count cannot tell whether one of
/// them was closed.
/// </description></item>
/// <item><description>
/// <b>An unsaved-work prompt.</b> Typing into the edit box and then closing
/// raises a real Win32 dialog — a SEPARATE top-level window, which is the
/// opposite shape from a WinUI ContentDialog living inside its owner's subtree.
/// Between the two subjects the suite now covers both.
/// </description></item>
/// </list>
/// </remarks>
internal static class Program
{
    private const string WindowClass = "TestAppWnd";
    private const string PromptClass = "TestAppPromptWnd";
    private const string EditClass = "Edit";
    private const string ButtonClass = "Button";
    private const string StaticClass = "Static";

    private const int WM_CREATE = 0x0001;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private const int WM_COMMAND = 0x0111;

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const int COLOR_WINDOW = 5;
    private const int IDC_ARROW = 32512;
    private const int SW_SHOW = 5;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const uint GW_OWNER = 4;

    private const int IDC_STATIC = 1001;
    private const int IDC_EDIT = 1002;
    private const int IDC_BUTTON = 1003;
    private const int IDC_LABEL = 1004;
    private const int IDC_NEWWINDOW = 1005;

    private const int IDC_DISCARD = 2001;
    private const int IDC_KEEP = 2002;

    private const uint ES_AUTOHSCROLL = 0x0080;
    private const uint BS_PUSHBUTTON = 0x00000000;

    /// <summary>The label a teardown looks for. Ours, so it does not localize.</summary>
    private const string DiscardLabel = "Discard changes";

    private static readonly Native.WndProcDelegate MainProc = WndProc;
    private static readonly Native.WndProcDelegate PromptProc = PromptWndProc;

    /// <summary>How many top-level windows are open.</summary>
    /// <remarks>
    /// The message loop must end when the LAST one closes, not the first.
    /// Posting quit from any WM_DESTROY would take the whole application down
    /// with one window and destroy the multi-window behaviour this subject
    /// exists to provide.
    /// </remarks>
    private static int _openWindows;

    private static void Main()
    {
        IntPtr hInstance = Native.GetModuleHandleW(null);

        Native.WNDCLASSEXW wc = new()
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(MainProc),
            hInstance = hInstance,
            lpszClassName = WindowClass,
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
            hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
            style = CS_HREDRAW | CS_VREDRAW,
        };
        Native.RegisterClassExW(ref wc);

        Native.WNDCLASSEXW promptClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(PromptProc),
            hInstance = hInstance,
            lpszClassName = PromptClass,
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
            hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
            style = CS_HREDRAW | CS_VREDRAW,
        };
        Native.RegisterClassExW(ref promptClass);

        OpenWindow(hInstance);

        while (Native.GetMessageW(out Native.MSG msg, IntPtr.Zero, 0, 0))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    private static void OpenWindow(IntPtr hInstance)
    {
        IntPtr hWnd = Native.CreateWindowExW(
            0, WindowClass, "WindowsDriverCore Test App",
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, 500, 350,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        _openWindows++;

        Native.ShowWindow(hWnd, SW_SHOW);
        Native.UpdateWindow(hWnd);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CREATE:
            {
                IntPtr hInst = Native.GetModuleHandleW(null);

                Native.CreateWindowExW(0, StaticClass, "Status: Ready",
                    WS_CHILD | WS_VISIBLE, 10, 10, 460, 20,
                    hWnd, (IntPtr)IDC_STATIC, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, EditClass, string.Empty,
                    WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL,
                    10, 40, 460, 25,
                    hWnd, (IntPtr)IDC_EDIT, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, ButtonClass, "Click Me",
                    WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 10, 80, 120, 30,
                    hWnd, (IntPtr)IDC_BUTTON, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, ButtonClass, "New Window",
                    WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 140, 80, 120, 30,
                    hWnd, (IntPtr)IDC_NEWWINDOW, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, StaticClass, "Label for testing",
                    WS_CHILD | WS_VISIBLE, 10, 130, 460, 20,
                    hWnd, (IntPtr)IDC_LABEL, hInst, IntPtr.Zero);
                break;
            }

            case WM_COMMAND:
            {
                long control = wParam.ToInt64() & 0xFFFF;

                if (control == IDC_BUTTON)
                {
                    IntPtr status = Native.GetDlgItem(hWnd, IDC_STATIC);
                    Native.SetWindowTextW(status, "Clicked! Text: " + TextOf(hWnd, IDC_EDIT));
                }
                else if (control == IDC_NEWWINDOW)
                {
                    // A SECOND WINDOW IN THIS PROCESS, which is what makes a
                    // process count unable to see one of them close.
                    OpenWindow(Native.GetModuleHandleW(null));
                }

                break;
            }

            case WM_CLOSE:
            {
                // UNSAVED WORK HOLDS THE CLOSE. A prompt that does not actually
                // block is not the condition a teardown has to answer: it could
                // pass by outrunning the dialog rather than by finding it.
                if (TextOf(hWnd, IDC_EDIT).Length > 0)
                {
                    ShowDiscardPrompt(hWnd);
                    return IntPtr.Zero;
                }

                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;
            }

            case WM_DESTROY:
                if (--_openWindows <= 0)
                {
                    Native.PostQuitMessage(0);
                }

                break;

            default:
                break;
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>Raises the unsaved-work dialog for a window.</summary>
    /// <remarks>
    /// A SEPARATE top-level window, owned by the one being closed. That is what
    /// a Win32 application does, and it is deliberately the opposite shape from
    /// a WinUI ContentDialog, which has no window of its own and lives in its
    /// owner's UIA subtree. A teardown that only knows one of those shapes hangs
    /// on the other, which is how the shape mattered here in the first place.
    /// </remarks>
    private static void ShowDiscardPrompt(IntPtr owner)
    {
        IntPtr hInst = Native.GetModuleHandleW(null);

        IntPtr prompt = Native.CreateWindowExW(
            0, PromptClass, "Save changes?",
            WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT, 320, 140,
            owner, IntPtr.Zero, hInst, IntPtr.Zero);

        // Disabled while the dialog is up, which is what makes it modal and what
        // stops a teardown closing the owner behind the dialog's back.
        Native.EnableWindow(owner, false);
        Native.ShowWindow(prompt, SW_SHOW);
        Native.UpdateWindow(prompt);
    }

    private static IntPtr PromptWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CREATE:
            {
                IntPtr hInst = Native.GetModuleHandleW(null);

                Native.CreateWindowExW(0, StaticClass, "You have unsaved changes.",
                    WS_CHILD | WS_VISIBLE, 15, 15, 280, 20,
                    hWnd, IntPtr.Zero, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, ButtonClass, DiscardLabel,
                    WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 15, 55, 130, 30,
                    hWnd, (IntPtr)IDC_DISCARD, hInst, IntPtr.Zero);

                Native.CreateWindowExW(0, ButtonClass, "Cancel",
                    WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 160, 55, 120, 30,
                    hWnd, (IntPtr)IDC_KEEP, hInst, IntPtr.Zero);
                break;
            }

            case WM_COMMAND:
            {
                long control = wParam.ToInt64() & 0xFFFF;
                IntPtr owner = Native.GetWindow(hWnd, GW_OWNER);

                if (control == IDC_DISCARD)
                {
                    // Cleared first, so the owner's WM_CLOSE does not raise a
                    // second prompt on the way out.
                    Native.SetWindowTextW(Native.GetDlgItem(owner, IDC_EDIT), string.Empty);
                    Native.EnableWindow(owner, true);
                    Native.DestroyWindow(hWnd);
                    Native.DestroyWindow(owner);
                }
                else if (control == IDC_KEEP)
                {
                    Native.EnableWindow(owner, true);
                    Native.DestroyWindow(hWnd);
                }

                break;
            }

            default:
                break;
        }

        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static string TextOf(IntPtr hWnd, int controlId)
    {
        IntPtr control = Native.GetDlgItem(hWnd, controlId);
        int length = Native.GetWindowTextLengthW(control) + 1;
        StringBuilder text = new(length);

        // The return value is the count actually copied, and a control that has
        // gone reports zero rather than failing - so it decides whether there is
        // anything to read at all.
        int copied = Native.GetWindowTextW(control, text, length);

        return copied > 0 ? text.ToString() : string.Empty;
    }
}
