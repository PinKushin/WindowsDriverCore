# 006 — W3C WebDriver vs JSON Wire Protocol: The Two-Protocol Trap

## Date: 2026-08-02

## The Problem
The old Appium client (`Microsoft.WinAppDriver.Appium.WebDriver.1.0.1-Preview`) is built on **Selenium WebDriver 3.8**, which uses **JSON Wire Protocol (JWP)**. But it also partially supports **W3C WebDriver**. The client negotiates protocol during session creation. Our server returns W3C format responses. This mismatch causes cascading failures.

## Key Discovery: `/rect` Endpoint
The Selenium 3.8 client sends `GET /element/{id}/rect` for both `Location` and `Size` — this is the **W3C WebDriver** endpoint. We only had `/location` and `/size` (JSON Wire Protocol endpoints) → 404 → client deserializes error body as string → `InvalidCastException: Unable to cast object of type 'System.String' to 'Dictionary<string, object>'`.

**The `/rect` endpoint is the W3C equivalent of both `/location` and `/size`.** It returns `{x, y, width, height}` in one response.

## Endpoint Mapping

| Operation | W3C (GET) | JSON Wire Protocol (POST) |
|-----------|-----------|---------------------------|
| Location+Size | `GET /element/{id}/rect` | `POST /element/{id}/location` + `POST /element/{id}/size` |
| Location only | N/A | `POST /element/{id}/location` |
| Size only | N/A | `POST /element/{id}/size` |
| Enabled | `GET /element/{id}/enabled` | `POST /element/{id}/enabled` |
| Displayed | `GET /element/{id}/displayed` | `POST /element/{id}/enabled` |
| Text | `GET /element/{id}/text` | `POST /element/{id}/text` |
| Attribute | `GET /element/{id}/attribute/{name}` | `POST /element/{id}/attribute/{name}` |
| Selected | `GET /element/{id}/selected` | `POST /element/{id}/selected` |
| Tag Name | `GET /element/{id}/name` | `POST /element/{id}/name` |
| Screenshot | `GET /element/{id}/screenshot` | `POST /element/{id}/screenshot` |

## The Fix: DualGetPost
Every element property endpoint must accept **both GET and POST**:
```csharp
static void DualGetPost(WebApplication a, string pattern, Delegate handler)
{
    a.MapGet(pattern, handler);
    a.MapPost(pattern, handler);
}
```

## Why the Client Gets `System.String` for Error Responses
When the client sends POST to a GET-only endpoint, ASP.NET returns 405 Method Not Allowed. The error response body is a JSON string like `"Method Not Allowed"`. The old Appium client's `Response` class deserializes this into `Response.Value` as a `System.String`. Then `RemoteWebElement.get_Location` does `(Dictionary<string, object>)response.Value` → `InvalidCastException`.

## Files Changed
- `Routes/ElementRoutes.cs` — All element property routes use `DualGetPost`, added `/rect` endpoint
- `Program.cs` — Exception handler uses `AllowStatusCode404Response = true`
