# 010 — Location/Size Response Format and Serialization

## Date: 2026-08-02

## The Bug
`ElementInteractor.GetCoordinates()` was changed to return JSON (`JsonSerializer.Serialize(new { x, y })`) but the routes still did `Split(',')` on the result → `FormatException` or `IndexOutOfRangeException`.

## The Fix
Routes now parse the JSON properly:
```csharp
var json = interactor.GetCoordinates(elementId);
var doc = JsonDocument.Parse(json);
var x = doc.RootElement.GetProperty("x").GetInt32();
var y = doc.RootElement.GetProperty("y").GetInt32();
return Results.Json(new { value = new { x, y } });
```

## Response Format
All location/size responses must follow this exact structure:
```json
{"value": {"x": 898, "y": 577}}
{"value": {"width": 100, "height": 50}}
{"value": {"x": 898, "y": 577, "width": 100, "height": 50}}  // /rect
```

## Why the Old Client Needs This
The old Appium client (Selenium 3.8) deserializes responses using Newtonsoft.Json. The `Response` class has `Value` typed as `object`. When the response is `{"value": {"x": 898, "y": 577}}`, `Value` becomes a `JObject`. The client code then does `response.Value as Dictionary<string, object>` which returns `null` (JObject doesn't implement IDictionary). This causes `NullReferenceException` for methods NOT overridden by the Appium driver.

**Methods overridden by Appium driver** (work correctly):
- `Location` → works
- `Size` → works

**Methods NOT overridden** (use base Selenium implementation):
- `LocationOnScreenOnceScrolledIntoView` → fails with NullReferenceException

## Important: `Results.Json` vs `WebDriverResponse<T>`
- `Results.Json(new { value = new { x, y } })` → serializes anonymous type directly
- `Results.Json(new WebDriverResponse<object?>(null))` → serializes record with `Value` property
- Both produce `{"value": ...}` format, but the inner structure differs
- The anonymous type produces `{"value": {"x": 898, "y": 577}}` directly
- `WebDriverResponse<T>` produces `{"value": <T>}` where T is serialized

## Files Changed
- `Automation/ElementInteractor.cs` — `GetCoordinates()`, `GetSize()`, `GetLocationInView()` return JSON
- `Routes/ElementRoutes.cs` — Routes parse JSON, added `/rect` endpoint
