# 007 — COM Exception Handling: Stale Elements and Disabled Clicks

## Date: 2026-08-02

## The Problem
When an element becomes stale between `GetAndVerifyAlive()` and the actual COM call (e.g., `invoke.Invoke()`, `SetFocus()`), the COM call throws a `COMException` with HRESULT `0x80040200` (`UIA_E_ELEMENTNOTAVAILABLE`). This leaked through to the generic 500 error handler, returning raw COM error codes instead of proper WebDriver errors.

Similarly, clicking a disabled element threw a raw COM exception instead of `element not visible`.

## HRESULT Reference
| HRESULT | Constant | Meaning | WebDriver Error |
|---------|----------|---------|-----------------|
| `0x80040200` | `UIA_E_ELEMENTNOTAVAILABLE` | Element removed from tree | `stale element reference` (HTTP 404) |
| `0x80040201` | `UIA_E_ELEMENTNOTENABLED` | Element is disabled | `element not visible` (HTTP 500) |

## The Fix: COM Exception Wrappers
In `ElementInteractor.cs`, wrap all COM calls with try-catch:
```csharp
catch (COMException ex)
{
    if (IsElementNotAvailable(ex))
        throw new WebDriverException(ErrorType.StaleElementReference, "...");
    if (IsElementNotEnabled(ex) || !element.GetIsEnabled())
        throw new WebDriverException(ErrorType.ElementNotVisible, "Element is not enabled");
    throw new WebDriverException(ErrorType.UnknownError, $"An element command failed: 0x{ex.ErrorCode:X8}");
}
```

Also add `COMException` handling in the global exception handler (`Program.cs`) as a safety net for any COM exceptions that escape the interactor.

## Why `AllowStatusCode404Response = true`
The stale element error returns HTTP 404. ASP.NET's exception handler by default throws `InvalidOperationException` when the handler produces a 404 status code, because it thinks the handler failed. Setting `AllowStatusCode404Response = true` tells ASP.NET that 404 is an intentional status from the exception handler.

Without this, the exception handler itself throws, and the `DeveloperExceptionPageMiddleware` tries to display that error, which fails because the response body stream is already closed → `ObjectDisposedException: Cannot access a closed Stream`.

## Files Changed
- `Automation/ElementInteractor.cs` — COM exception wrappers on Click, SendKeys, Clear, GetText, GetAttribute, GetSelected
- `Program.cs` — Global COMException handler + `AllowStatusCode404Response = true`
