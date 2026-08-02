using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using WindowsDriverCore.Automation.Com;
using WindowsDriverCore.Automation.Raw;
using WindowsDriverCore.Automation;
using WindowsDriverCore.ErrorHandling;
using WindowsDriverCore.Messages;
using WindowsDriverCore.Sessions;
using WindowsDriverCore.Windows;

namespace WindowsDriverCore.Routes;

public static class ElementRoutes
{
    private const string W3cElementKey = "element-6066-11e4-a52e-4f735466cecf";

    private static void ValidateWindow(SessionContext session, IWindowFinder windowFinder)
    {
        if (!windowFinder.IsWindowValid(session.MainWindowHandle))
            throw new WebDriverException(ErrorType.UnknownError, "Currently selected window has been closed", 404);
    }

    public static void MapElementRoutes(this WebApplication app)
    {
        app.MapPost("/session/{sessionId}/element/active", (string sessionId, ISessionStore store, IWindowFinder windowFinder, ElementStore elementStore) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var automation = UIAutomationFactory.Create();
            int hr = automation.ElementFromHandle(session.MainWindowHandle, out IntPtr rootPtr);
            if (hr != 0 || rootPtr == IntPtr.Zero)
                throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

            using var condition = ConditionFactory.CreatePropertyCondition(UIAPropertyIds.UIA_HasKeyboardFocusPropertyId, true);
            var rawRoot = new RawAutomationElement(rootPtr);
            var focused = rawRoot.FindFirst(UIATreeScope.TreeScope_Descendants, condition.ConditionPtr);

            if (focused is null)
                return Results.Json(new { value = new Dictionary<string, string> { [W3cElementKey] = "", ["ELEMENT"] = "" } });

            var elementId = elementStore.Store(focused);
            return Results.Json(new { value = new Dictionary<string, string> { [W3cElementKey] = elementId, ["ELEMENT"] = elementId } });
        });

        app.MapPost("/session/{sessionId}/element", (string sessionId, ElementRequest request, ISessionStore store, IElementFinder finder, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var elementId = finder.FindElement(session.MainWindowHandle, request.Using, request.Value);
            return Results.Json(new { value = new Dictionary<string, string> { [W3cElementKey] = elementId, ["ELEMENT"] = elementId } });
        });

        app.MapPost("/session/{sessionId}/elements", (string sessionId, ElementRequest request, ISessionStore store, IElementFinder finder, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var elementIds = finder.FindElements(session.MainWindowHandle, request.Using, request.Value);
            var elements = elementIds.Select(id => new Dictionary<string, string> { [W3cElementKey] = id, ["ELEMENT"] = id }).ToList();
            return Results.Json(new { value = elements });
        });

        app.MapPost("/session/{sessionId}/element/{elementId}/element", (string sessionId, string elementId, ElementRequest request, ISessionStore store, IElementFinder finder, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var childId = finder.FindElementInElement(elementId, request.Using, request.Value);
            return Results.Json(new { value = new Dictionary<string, string> { [W3cElementKey] = childId, ["ELEMENT"] = childId } });
        });

        app.MapPost("/session/{sessionId}/element/{elementId}/elements", (string sessionId, string elementId, ElementRequest request, ISessionStore store, IElementFinder finder, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var childIds = finder.FindElementsInElement(elementId, request.Using, request.Value);
            var elements = childIds.Select(id => new Dictionary<string, string> { [W3cElementKey] = id, ["ELEMENT"] = id }).ToList();
            return Results.Json(new { value = elements });
        });

        app.MapPost("/session/{sessionId}/element/{elementId}/click", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            interactor.Click(elementId);
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/element/{elementId}/value", async (string sessionId, string elementId, HttpRequest request, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            string text = "";
            using (var reader = new StreamReader(request.Body))
            {
                var bodyStr = await reader.ReadToEndAsync();
                if (!string.IsNullOrEmpty(bodyStr))
                {
                    try
                    {
                        var body = JsonSerializer.Deserialize<JsonElement>(bodyStr);
                        text = body.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                    }
                    catch { }
                }
            }
            interactor.SendKeys(elementId, text);
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/element/{elementId}/clear", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            interactor.Clear(elementId);
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/text", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var text = interactor.GetText(elementId);
            return Results.Json(new WebDriverResponse<string>(text));
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/enabled", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var enabled = interactor.GetEnabled(elementId);
            return Results.Json(new WebDriverResponse<bool>(enabled));
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/displayed", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var displayed = interactor.GetDisplayed(elementId);
            return Results.Json(new WebDriverResponse<bool>(displayed));
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/name", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var tagName = interactor.GetTagName(elementId);
            return Results.Json(new WebDriverResponse<string>(tagName));
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/attribute/{attributeName}", (string sessionId, string elementId, string attributeName, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var value = interactor.GetAttribute(elementId, attributeName);
            return Results.Json(new WebDriverResponse<object?>(value));
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/selected", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var selected = interactor.GetSelected(elementId);
            return Results.Json(new { value = bool.Parse(selected) });
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/location", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var coords = interactor.GetCoordinates(elementId);
            var parts = coords.Split(',');
            return Results.Json(new { value = new { x = int.Parse(parts[0]), y = int.Parse(parts[1]) } });
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/location_in_view", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var coords = interactor.GetLocationInView(elementId);
            var parts = coords.Split(',');
            return Results.Json(new { value = new { x = int.Parse(parts[0]), y = int.Parse(parts[1]) } });
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/size", (string sessionId, string elementId, ISessionStore store, IElementInteractor interactor, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var size = interactor.GetSize(elementId);
            var parts = size.Split(',');
            return Results.Json(new { value = new { width = int.Parse(parts[0]), height = int.Parse(parts[1]) } });
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/equals/{otherElementId}", (string sessionId, string elementId, string otherElementId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var areEqual = elementId == otherElementId;
            return Results.Json(new { value = areEqual });
        });

        app.MapGet("/session/{sessionId}/element/{elementId}/screenshot", (string sessionId, string elementId, ISessionStore store, IWindowFinder windowFinder, ElementStore elementStore) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var element = elementStore.Get(elementId);
            if (element is null)
                throw new WebDriverException(ErrorType.UnknownError,
                    "An element command failed because the referenced element is no longer attached to the DOM.");

            try
            {
                var bounds = element.GetBoundingRectangle();
                if (bounds.IsEmpty)
                    throw new WebDriverException(ErrorType.UnknownError, "Element is not displayed");

                var boundsInt = new System.Drawing.Rectangle(
                    (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height);

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
                throw new WebDriverException(ErrorType.UnknownError,
                    "An element command failed because the referenced element is no longer attached to the DOM.");
            }
        });

        app.MapGet("/session/{sessionId}/source", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            var automation = UIAutomationFactory.Create();
            int hr = automation.ElementFromHandle(session.MainWindowHandle, out IntPtr rootPtr);
            if (hr != 0 || rootPtr == IntPtr.Zero)
                throw new WebDriverException(ErrorType.UnknownError, "Unable to get automation element from handle");

            var xml = BuildSourceXml(new RawAutomationElement(rootPtr));
            return Results.Json(new WebDriverResponse<string>(xml));
        });

        app.MapPost("/session/{sessionId}/back", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            Win32.SetForegroundWindow(session.MainWindowHandle);
            Thread.Sleep(100);
            SendAltLeft();
            return Results.Json(new WebDriverResponse<object?>(null));
        });

        app.MapPost("/session/{sessionId}/forward", (string sessionId, ISessionStore store, IWindowFinder windowFinder) =>
        {
            var session = store.Get(sessionId);
            if (session is null)
                throw new WebDriverException(ErrorType.NoSuchSession, "no such session", 404);

            ValidateWindow(session, windowFinder);

            Win32.SetForegroundWindow(session.MainWindowHandle);
            Thread.Sleep(100);
            SendAltRight();
            return Results.Json(new WebDriverResponse<object?>(null));
        });
    }

    private static string BuildSourceXml(RawAutomationElement element, int depth = 0)
    {
        var indent = new string(' ', depth * 2);
        var tag = element.GetControlTypeName().Replace("ControlType.", "");
        var name = EscapeXml(element.GetName());
        var automationId = element.GetAutomationId();
        var className = element.GetClassName();

        var sb = new System.Text.StringBuilder();
        sb.Append($"{indent}<{tag}");

        if (!string.IsNullOrEmpty(automationId))
            sb.Append($" AutomationId=\"{EscapeXml(automationId)}\"");
        if (!string.IsNullOrEmpty(className))
            sb.Append($" ClassName=\"{EscapeXml(className)}\"");
        if (!string.IsNullOrEmpty(name))
            sb.Append($" Name=\"{name}\"");

        var children = element.GetChildren();
        if (children.Count == 0)
        {
            sb.Append(" />");
        }
        else
        {
            sb.AppendLine(">");
            foreach (var child in children)
            {
                sb.AppendLine(BuildSourceXml(child, depth + 1));
            }
            sb.Append($"{indent}</{tag}>");
        }

        return sb.ToString();
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static void SendAltLeft()
    {
        var inputs = new Win32.INPUT[]
        {
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x12, dwFlags = 0 } } },
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x25, dwFlags = 0 } } },
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x25, dwFlags = Win32.KEYEVENTF_KEYUP } } },
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x12, dwFlags = Win32.KEYEVENTF_KEYUP } } },
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendAltRight()
    {
        var inputs = new Win32.INPUT[]
        {
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x12, dwFlags = 0 } } },
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x27, dwFlags = 0 } } },
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x27, dwFlags = Win32.KEYEVENTF_KEYUP } } },
            new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0x12, dwFlags = Win32.KEYEVENTF_KEYUP } } },
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }
}
