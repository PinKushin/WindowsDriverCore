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
                        "A request to switch to a window could not be satisfied because the window could not be found.", 404);
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
            Win32.SendMessage(hWnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            session.MainWindowHandle = IntPtr.Zero;

            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapGet("/session/{sessionId}/window/rect", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            Win32.GetWindowRect(session.MainWindowHandle, out var rect);
            return Results.Json(new WebDriverResponse<object?>(new
            {
                x = rect.Left,
                y = rect.Top,
                width = rect.Right - rect.Left,
                height = rect.Bottom - rect.Top
            }));
        });

        app.MapPost("/session/{sessionId}/window/rect", async (string sessionId, HttpRequest request, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = GetSessionOrThrow(store, sessionId);

            if (!windowFinder.IsWindowValid(session.MainWindowHandle))
                throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);

            int x = 0, y = 0, width = 0, height = 0;
            using (var reader = new StreamReader(request.Body))
            {
                var bodyStr = await reader.ReadToEndAsync();
                if (!string.IsNullOrEmpty(bodyStr))
                {
                    try
                    {
                        var body = JsonSerializer.Deserialize<JsonElement>(bodyStr);
                        if (body.TryGetProperty("x", out var xProp)) x = xProp.GetInt32();
                        if (body.TryGetProperty("y", out var yProp)) y = yProp.GetInt32();
                        if (body.TryGetProperty("width", out var wProp)) width = wProp.GetInt32();
                        if (body.TryGetProperty("height", out var hProp)) height = hProp.GetInt32();
                    }
                    catch { }
                }
            }

            if (width > 0 && height > 0)
                Win32.MoveWindow(session.MainWindowHandle, x, y, width, height, true);
            else if (x != 0 || y != 0)
                Win32.SetWindowPos(session.MainWindowHandle, IntPtr.Zero, x, y, 0, 0,
                    Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);

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

        // Snapshot existing ApplicationFrameWindows before activation (for WinUI3 apps)
        var existingFrameWindows = new HashSet<IntPtr>();
        Win32.EnumWindows((hWnd, _) =>
        {
            if (windowFinder.GetWindowClassName(hWnd) == "ApplicationFrameWindow")
                existingFrameWindows.Add(hWnd);
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
            // that contain a child process spawned by our activation
            try
            {
                var candidateHwnd = windowFinder.FindNewApplicationFrameWindow(processId, existingFrameWindows);
                if (candidateHwnd != IntPtr.Zero)
                    return candidateHwnd;
            }
            catch { }

            Thread.Sleep(WindowPollIntervalMs);
        }

        return IntPtr.Zero;
    }
}
