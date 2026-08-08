using System.IO;
using System.Text;
using System.Text.Json;
using WindowsDriverCore.Applications;
using WindowsDriverCore.ErrorHandling;
using WindowsDriverCore.Messages;
using WindowsDriverCore.Sessions;
using WindowsDriverCore.Windows;

namespace WindowsDriverCore.Routes;

public static class SessionRoutes
{
    private const int WindowWaitTimeoutMs = 10000;
    private const int WindowPollIntervalMs = 100;

    private static void DualGetPost(WebApplication a, string pattern, Delegate handler)
    {
        a.MapGet(pattern, handler);
        a.MapPost(pattern, handler);
    }

    public static void MapSessionRoutes(this WebApplication app)
    {
        app.MapPost("/session", (SessionRequest request, ISessionStore store, IAppLauncher launcher, IWindowFinder windowFinder) =>
        {
            var caps = request.Capabilities?.AlwaysMatch ?? request.DesiredCapabilities ?? new Dictionary<string, object>();

            var hasApp = caps.TryGetValue("app", out var appIdObj) && appIdObj != null;
            var hasTopLevelWindow = caps.TryGetValue("appTopLevelWindow", out var windowObj) && windowObj != null;

            if (!hasApp && !hasTopLevelWindow)
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Bad capabilities. Specify either app or appTopLevelWindow to create a session", 400);

            if (hasApp && hasTopLevelWindow)
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Bad capabilities. Specify either app or appTopLevelWindow to create a session", 400);

            if (hasApp)
            {
                var appId = appIdObj!.ToString()!;
                if (string.IsNullOrEmpty(appId))
                    throw new WebDriverException(ErrorType.InvalidArgument,
                        "Capability: app cannot be empty", 400);

                if (AppLauncher.IsDesktopAppId(appId))
                    return CreateDesktopSession(store, caps);

                var arguments = caps.TryGetValue("appArguments", out var argObj) ? argObj?.ToString() : null;
                var workingDir = caps.TryGetValue("appWorkingDir", out var dirObj) ? dirObj?.ToString() : null;

                int processId;
                bool isUwpApp = appId.Contains('!');
                try
                {
                    processId = launcher.Launch(appId, arguments, workingDir);
                }
                catch (FileNotFoundException)
                {
                    throw new WebDriverException(ErrorType.UnknownError,
                        "The system cannot find the file specified", 500);
                }
                catch (InvalidOperationException ex)
                {
                    throw new WebDriverException(ErrorType.UnknownError, ex.Message, 500);
                }

                if (processId == 0)
                {
                    if (isUwpApp)
                        throw new WebDriverException(ErrorType.UnknownError,
                            "Value does not fall within the expected range.");
                    throw new WebDriverException(ErrorType.UnknownError,
                        "Could not find main window for application");
                }

                var mainWindowHandle = WaitForMainWindow(windowFinder, processId);
                if (mainWindowHandle == IntPtr.Zero)
                {
                    launcher.Close(processId);
                    if (isUwpApp)
                        throw new WebDriverException(ErrorType.UnknownError,
                            "Value does not fall within the expected range.");
                    throw new WebDriverException(ErrorType.UnknownError,
                        $"Could not find main window for process {processId}");
                }

                // Resolve the actual window PID (UWP COM activation returns a broker PID, not the app PID)
                Win32.GetWindowThreadProcessId(mainWindowHandle, out var actualWindowPid);
                var sessionPid = (int)actualWindowPid != 0 ? (int)actualWindowPid : processId;

                var session = store.Create(sessionPid, mainWindowHandle, caps);
                var sessionInfo = BuildSessionInfo(session);
                return Results.Json(new WebDriverResponse<SessionInfo>(sessionInfo));
            }
            else
            {
                return CreateSessionFromWindowHandle(windowObj!.ToString()!, store, caps);
            }
        });

        app.MapDelete("/session/{sessionId}", (string sessionId, ISessionStore store, IAppLauncher launcher) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            // Always close the window handle directly (works for all app types including Explorer)
            launcher.CloseWindow(session.MainWindowHandle);

            if (!AppLauncher.IsDesktopAppId(session.Capabilities.GetValueOrDefault("app")?.ToString() ?? ""))
                launcher.Close(session.ProcessId);

            store.Remove(sessionId);
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapGet("/sessions", (ISessionStore store) =>
        {
            var sessions = store.GetAll();
            var sessionList = sessions.Select(s => new
            {
                id = s.SessionId,
                capabilities = s.Capabilities
            }).ToList();

            return Results.Json(new { status = 0, value = sessionList });
        });

        app.MapPost("/session/{sessionId}/appium/app/close", (string sessionId, ISessionStore store, IAppLauncher launcher, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            if (!AppLauncher.IsDesktopAppId(session.Capabilities.GetValueOrDefault("app")?.ToString() ?? ""))
            {
                launcher.Close(session.ProcessId);
                session.ProcessId = 0;
            }

            session.MainWindowHandle = IntPtr.Zero;

            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/appium/app/launch", (string sessionId, ISessionStore store, IAppLauncher launcher, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            var appId = session.Capabilities.GetValueOrDefault("app")?.ToString();
            if (string.IsNullOrEmpty(appId))
                throw new WebDriverException(ErrorType.InvalidArgument, "No app capability set for this session", 400);

            if (AppLauncher.IsDesktopAppId(appId))
                return Results.Json(new WebDriverResponse<object?>(null));

            var arguments = session.Capabilities.GetValueOrDefault("appArguments")?.ToString();
            var workingDir = session.Capabilities.GetValueOrDefault("appWorkingDir")?.ToString();

            if (session.ProcessId != 0)
            {
                try { launcher.Close(session.ProcessId); } catch { }
            }

            int processId;
            try
            {
                processId = launcher.Launch(appId, arguments, workingDir);
            }
            catch (FileNotFoundException)
            {
                throw new WebDriverException(ErrorType.UnknownError, "The system cannot find the file specified", 500);
            }
            catch (InvalidOperationException ex)
            {
                throw new WebDriverException(ErrorType.UnknownError, ex.Message, 500);
            }

            if (processId == 0)
                throw new WebDriverException(ErrorType.UnknownError, "Could not find main window for application");

            var mainWindowHandle = WaitForMainWindow(windowFinder, processId);
            if (mainWindowHandle == IntPtr.Zero)
            {
                launcher.Close(processId);
                throw new WebDriverException(ErrorType.UnknownError, $"Could not find main window for process {processId}");
            }

            // Resolve the actual window PID (UWP COM activation returns a broker PID, not the app PID)
            Win32.GetWindowThreadProcessId(mainWindowHandle, out var actualWindowPid);
            session.ProcessId = (int)actualWindowPid != 0 ? (int)actualWindowPid : processId;
            session.MainWindowHandle = mainWindowHandle;

            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapGet("/session/{sessionId}/title", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            if (AppLauncher.IsDesktopAppId(session.Capabilities.GetValueOrDefault("app")?.ToString() ?? ""))
                return Results.Json(new WebDriverResponse<string>("Desktop"));

            var title = windowFinder.GetWindowTitle(session.MainWindowHandle);
            return Results.Json(new WebDriverResponse<string>(title));
        });

        DualGetPost(app, "/session/{sessionId}/orientation", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            // Desktop apps are always landscape orientation
            return Results.Json(new WebDriverResponse<string>("LANDSCAPE"));
        });

        DualGetPost(app, "/session/{sessionId}/screenshot", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            try
            {
                Win32.GetWindowRect(session.MainWindowHandle, out var rect);
                var boundsInt = new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);

                if (boundsInt.Width <= 0 || boundsInt.Height <= 0)
                    throw new WebDriverException(ErrorType.UnknownError, "Window has no visible area");

                using var bmp = new System.Drawing.Bitmap(boundsInt.Width, boundsInt.Height);
                using var graphics = System.Drawing.Graphics.FromImage(bmp);
                graphics.CopyFromScreen(boundsInt.Location, System.Drawing.Point.Empty, boundsInt.Size);

                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var base64 = Convert.ToBase64String(ms.ToArray());
                return Results.Json(new WebDriverResponse<string>(base64));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                throw new WebDriverException(ErrorType.UnknownError, "Unable to capture screenshot");
            }
        });

        app.MapGet("/session/{sessionId}/window/handle", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            var handle = session.MainWindowHandle.ToString("x");
            return Results.Json(new WebDriverResponse<string>(handle));
        });

        app.MapGet("/session/{sessionId}/window", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            var handle = session.MainWindowHandle.ToString("x");
            return Results.Json(new WebDriverResponse<string>(handle));
        });

        app.MapGet("/session/{sessionId}/window/handles", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                return Results.Json(new WebDriverResponse<List<string>>(new List<string>()));

            // For desktop session (processId == 0), return empty handles as per WebDriver spec
            if (session.ProcessId == 0)
                return Results.Json(new WebDriverResponse<List<string>>(new List<string>()));

            var handles = new List<string>();
            handles.Add(session.MainWindowHandle.ToString("x"));
            Win32.EnumChildWindows(session.MainWindowHandle, (hWnd, _) =>
            {
                handles.Add(hWnd.ToString("x"));
                return true;
            }, IntPtr.Zero);

            return Results.Json(new WebDriverResponse<List<string>>(handles));
        });

        app.MapPost("/session/{sessionId}/window", async (string sessionId, HttpRequest request, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            string name = "";
            request.Body.Position = 0;
            using (var reader = new StreamReader(request.Body))
            {
                var bodyStr = await reader.ReadToEndAsync();
                if (!string.IsNullOrEmpty(bodyStr))
                {
                    try
                    {
                        var body = JsonSerializer.Deserialize<JsonElement>(bodyStr);
                        name = body.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(name))
                            name = body.TryGetProperty("handle", out var handleProp) ? handleProp.GetString() ?? "" : "";
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(name))
                throw new WebDriverException(ErrorType.UnknownError,
                    "Missing Command Parameter: name", 400);

            if (!long.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out var handleValue))
            {
                if (long.TryParse(name, out handleValue))
                {
                    if (handleValue < 0)
                        throw new WebDriverException(ErrorType.UnknownError,
                            "String cannot contain a minus sign if the base is not 10.", 400);
                }
                else
                {
                    throw new WebDriverException(ErrorType.UnknownError,
                        $"Failed to parse window handle: {name}", 400);
                }
            }

            var hWnd = new IntPtr(handleValue);

            if (!windowFinder.IsWindowValid(hWnd))
                throw new WebDriverException(ErrorType.UnknownError,
                    "A request to switch to a window could not be satisfied because the window could not be found.", 404);

            if (session.ProcessId != 0)
            {
                var targetPid = windowFinder.GetWindowProcessId(hWnd);
                if (targetPid != session.ProcessId)
                    throw new WebDriverException(ErrorType.UnknownError,
                        "Window handle does not belong to the same process/application", 400);
            }

            if (!windowFinder.IsTopLevelWindow(hWnd))
                throw new WebDriverException(ErrorType.UnknownError,
                    $"{name} is not a top level window handle", 400);

            session.MainWindowHandle = hWnd;
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapDelete("/session/{sessionId}/window", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            var hWnd = session.MainWindowHandle;
            Win32.PostMessage(hWnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            session.MainWindowHandle = IntPtr.Zero;

            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapGet("/session/{sessionId}/window/rect", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            Win32.GetWindowRect(session.MainWindowHandle, out var rect);
            var dpiScale = Win32.GetDpiScale(session.MainWindowHandle);
            Console.WriteLine($"[GetWindowRect] hwnd={session.MainWindowHandle.ToInt64():X} raw=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) dpi={dpiScale}");
            return Results.Json(new WebDriverResponse<object?>(new
            {
                x = (int)(rect.Left / dpiScale),
                y = (int)(rect.Top / dpiScale),
                width = (int)((rect.Right - rect.Left) / dpiScale),
                height = (int)((rect.Bottom - rect.Top) / dpiScale)
            }));
        });

        app.MapPost("/session/{sessionId}/window/rect", async (string sessionId, HttpRequest request, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            int x = 0, y = 0, width = 0, height = 0;
            bool hasX = false, hasY = false, hasWidth = false, hasHeight = false;
            using (var reader = new StreamReader(request.Body))
            {
                var bodyStr = await reader.ReadToEndAsync();
                if (!string.IsNullOrEmpty(bodyStr))
                {
                    try
                    {
                        var body = JsonSerializer.Deserialize<JsonElement>(bodyStr);
                        if (body.TryGetProperty("x", out var xProp)) { x = xProp.GetInt32(); hasX = true; }
                        if (body.TryGetProperty("y", out var yProp)) { y = yProp.GetInt32(); hasY = true; }
                        if (body.TryGetProperty("width", out var wProp)) { width = wProp.GetInt32(); hasWidth = true; }
                        if (body.TryGetProperty("height", out var hProp)) { height = hProp.GetInt32(); hasHeight = true; }
                    }
                    catch { }
                }
            }

            var dpiScale = Win32.GetDpiScale(session.MainWindowHandle);
            Win32.GetWindowRect(session.MainWindowHandle, out var currentRect);
            var curX = hasX ? (int)(x * dpiScale) : currentRect.Left;
            var curY = hasY ? (int)(y * dpiScale) : currentRect.Top;
            var curW = hasWidth ? (int)(width * dpiScale) : currentRect.Right - currentRect.Left;
            var curH = hasHeight ? (int)(height * dpiScale) : currentRect.Bottom - currentRect.Top;

            if (hasX || hasY || hasWidth || hasHeight)
            {
                Win32.MoveWindow(session.MainWindowHandle, curX, curY, curW, curH, true);
            }

            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/window/maximize", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            Win32.ShowWindow(session.MainWindowHandle, Win32.SW_MAXIMIZE);
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/window/minimize", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            Win32.ShowWindow(session.MainWindowHandle, Win32.SW_MINIMIZE);
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/window/restore", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            Win32.ShowWindow(session.MainWindowHandle, Win32.SW_RESTORE);
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/timeouts", async (string sessionId, HttpRequest request, ISessionStore store) =>
        {
            await HandleTimeoutRequest(sessionId, request, store);
        });

        app.MapPost("/session/{sessionId}/timeout", async (string sessionId, HttpRequest request, ISessionStore store) =>
        {
            await HandleTimeoutRequest(sessionId, request, store);
        });

        app.MapPost("/session/{sessionId}/keys", async (string sessionId, HttpRequest request, ISessionStore store) =>
        {
            var session = GetSessionOrThrow(store, sessionId);
            request.Body.Position = 0;
            using var reader = new StreamReader(request.Body);
            var bodyStr = await reader.ReadToEndAsync();
            if (string.IsNullOrEmpty(bodyStr))
                return Results.Json(new WebDriverResponse<object?>(null));

            var body = JsonSerializer.Deserialize<JsonElement>(bodyStr);
            var keySequence = new List<string>();
            if (body.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueProp.EnumerateArray())
                {
                    keySequence.Add(item.GetString() ?? "");
                }
            }

            KeyboardSimulator.SendKeySequence(keySequence);
            return Results.Json(new WebDriverResponse<object?>(null));
        });
    }

    private static async Task<IResult> HandleTimeoutRequest(string sessionId, HttpRequest request, ISessionStore store)
    {
        var session = GetSessionOrThrow(store, sessionId);

        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body);
        var bodyStr = await reader.ReadToEndAsync();

        if (string.IsNullOrEmpty(bodyStr))
            return Results.Json(new WebDriverResponse<object?>(null));

        var body = JsonSerializer.Deserialize<JsonElement>(bodyStr);

        if (body.TryGetProperty("type", out var typeProp))
        {
            var type = typeProp.GetString() ?? "";

            if (type.Equals("page load", StringComparison.OrdinalIgnoreCase))
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Unexpected error. Unimplemented Command: page load timeout type is not supported", 400);

            if (type.Equals("script", StringComparison.OrdinalIgnoreCase))
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Unexpected error. Unimplemented Command: script timeout type is not supported", 400);
        }

        if (body.TryGetProperty("ms", out var msProp))
        {
            long ms = msProp.TryGetInt64(out var longVal) ? longVal : (long)msProp.GetDouble();
            if (ms < 0)
                throw new WebDriverException(ErrorType.UnknownError,
                    $"Bad Command Parameter: ms:{ms}, type:implicit", 400);
        }

        // Selenium 3 JSON Wire Protocol sends {"implicit": <ms>}, {"pageLoad": <ms>}, {"script": <ms>} format
        foreach (var prop in body.EnumerateObject())
        {
            var propName = prop.Name;
            if (propName.Equals("pageLoad", StringComparison.OrdinalIgnoreCase) || propName.Equals("page load", StringComparison.OrdinalIgnoreCase))
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Unexpected error. Unimplemented Command: page load timeout type is not supported", 400);

            if (propName.Equals("script", StringComparison.OrdinalIgnoreCase))
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Unexpected error. Unimplemented Command: script timeout type is not supported", 400);

            if (prop.Value.ValueKind == JsonValueKind.Number)
            {
                long ms = prop.Value.TryGetInt64(out var longVal) ? longVal : (long)prop.Value.GetDouble();
                if (ms < 0)
                    throw new WebDriverException(ErrorType.UnknownError,
                        $"Bad Command Parameter: ms:{ms}, type:{prop.Name}", 400);
            }
        }

        return Results.Json(new WebDriverResponse<object?>(null));
    }

    private static SessionContext GetSessionOrThrow(ISessionStore store, string sessionId)
    {
        var session = store.Get(sessionId);
        if (session is null)
            throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);
        return session;
    }

    private static IResult CreateDesktopSession(ISessionStore store, Dictionary<string, object> caps)
    {
        var desktopWindow = Win32.GetDesktopWindow();
        var session = store.Create(0, desktopWindow, caps);
        var sessionInfo = BuildSessionInfo(session);
        return Results.Json(new WebDriverResponse<SessionInfo>(sessionInfo));
    }

    private static IResult CreateSessionFromWindowHandle(string windowHandleStr, ISessionStore store, Dictionary<string, object> caps)
    {
        if (string.IsNullOrEmpty(windowHandleStr))
            throw new WebDriverException(ErrorType.InvalidArgument,
                "Capability: appTopLevelWindow cannot be empty", 400);

        if (!long.TryParse(windowHandleStr, System.Globalization.NumberStyles.HexNumber, null, out var handleValue))
        {
            if (long.TryParse(windowHandleStr, out handleValue))
            {
                if (handleValue < 0)
                    throw new WebDriverException(ErrorType.UnknownError,
                        "String cannot contain a minus sign if the base is not 10.", 400);
            }
            else
            {
                throw new WebDriverException(ErrorType.UnknownError,
                    $"Failed to parse window handle: {windowHandleStr}", 400);
            }
        }

        var hWnd = new IntPtr(handleValue);
        if (!Win32.IsWindow(hWnd))
            throw new WebDriverException(ErrorType.InvalidArgument,
                "Cannot find active window specified by capabilities: appTopLevelWindow", 400);

        // Verify the owning process is still alive (stale handle from closed app)
        Win32.GetWindowThreadProcessId(hWnd, out var ownerPid);
        if (ownerPid != 0)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById((int)ownerPid);
                if (proc.HasExited)
                    throw new WebDriverException(ErrorType.InvalidArgument,
                        "Cannot find active window specified by capabilities: appTopLevelWindow", 400);
            }
            catch (ArgumentException)
            {
                throw new WebDriverException(ErrorType.InvalidArgument,
                    "Cannot find active window specified by capabilities: appTopLevelWindow", 400);
            }
        }

        Win32.GetWindowThreadProcessId(hWnd, out var pid);
        var session = store.Create((int)pid, hWnd, caps);
        var sessionInfo = BuildSessionInfo(session);
        return Results.Json(new WebDriverResponse<SessionInfo>(sessionInfo));
    }

    private static SessionInfo BuildSessionInfo(SessionContext session)
    {
        var responseCaps = new Dictionary<string, object>
        {
            ["platformName"] = "Windows"
        };

        foreach (var kvp in session.Capabilities)
        {
            responseCaps[kvp.Key] = kvp.Value;
        }

        return new SessionInfo(session.SessionId, responseCaps);
    }

    private static IntPtr WaitForMainWindow(IWindowFinder windowFinder, int processId)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(WindowWaitTimeoutMs);

        // Snapshot existing windows before launch
        var existingFrameWindows = new HashSet<IntPtr>();
        var existingCabinetWindows = new HashSet<IntPtr>();
        Win32.EnumWindows((hWnd, _) =>
        {
            var cls = windowFinder.GetWindowClassName(hWnd);
            if (cls == "ApplicationFrameWindow")
                existingFrameWindows.Add(hWnd);
            else if (cls == "CabinetWClass")
                existingCabinetWindows.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        while (DateTime.UtcNow < deadline)
        {
            var hWnd = windowFinder.FindWindowByProcessId(processId);
            if (hWnd != IntPtr.Zero)
                return hWnd;

            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(processId);
                proc.Refresh();
                if (proc.MainWindowHandle != IntPtr.Zero)
                    return proc.MainWindowHandle;
            }
            catch { }

            try
            {
                foreach (var childId in Win32.GetChildProcessIds(processId))
                {
                    hWnd = windowFinder.FindWindowByProcessId(childId);
                    if (hWnd != IntPtr.Zero)
                        return hWnd;

                    try
                    {
                        var childProc = System.Diagnostics.Process.GetProcessById(childId);
                        childProc.Refresh();
                        if (childProc.MainWindowHandle != IntPtr.Zero)
                            return childProc.MainWindowHandle;
                    }
                    catch { }
                }
            }
            catch { }

            // Fallback for WinUI3 apps: find newly-appeared ApplicationFrameWindows
            try
            {
                var candidateHwnd = windowFinder.FindNewApplicationFrameWindow(processId, existingFrameWindows);
                if (candidateHwnd != IntPtr.Zero)
                    return candidateHwnd;
            }
            catch { }

            // Fallback for Explorer/system apps: find newly-appeared CabinetWClass windows
            // Explorer.exe reuses the shell process, so FindWindowByProcessId can't find it
            try
            {
                var cabinetHwnd = FindNewCabinetWindow(existingCabinetWindows);
                if (cabinetHwnd != IntPtr.Zero)
                    return cabinetHwnd;
            }
            catch { }

            Thread.Sleep(WindowPollIntervalMs);
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindNewCabinetWindow(HashSet<IntPtr> existing)
    {
        IntPtr found = IntPtr.Zero;
        Win32.EnumWindows((hWnd, _) =>
        {
            if (existing.Contains(hWnd))
                return true;
            var cls = new System.Text.StringBuilder(256);
            Win32.GetClassName(hWnd, cls, 256);
            if (cls.ToString() == "CabinetWClass" && Win32.IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
