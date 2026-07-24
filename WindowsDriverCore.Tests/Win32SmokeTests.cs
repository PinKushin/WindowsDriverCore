using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WindowsDriverCore.Tests;

[TestClass]
public class Win32SmokeTests
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:4723") };
    private string? _sessionId;
    private static readonly string TestAppPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "TestApp", "bin", "Debug", "net10.0-windows", "TestApp.exe"));

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_sessionId is not null)
        {
            try { await _http.DeleteAsync($"/session/{_sessionId}"); } catch { }
            _sessionId = null;
        }
    }

    private async Task CreateSession(string app)
    {
        var body = new { capabilities = new { alwaysMatch = new { app } } };
        var response = await _http.PostAsJsonAsync("/session", body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        _sessionId = json.GetProperty("value").GetProperty("sessionId").GetString();
    }

    private async Task<JsonElement> Get(string path)
    {
        var response = await _http.GetAsync($"/session/{_sessionId}{path}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> Post(string path, object? body = null)
    {
        var response = body is null
            ? await _http.PostAsJsonAsync($"/session/{_sessionId}{path}", new { })
            : await _http.PostAsJsonAsync($"/session/{_sessionId}{path}", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> FindElement(string strategy, string value)
    {
        var result = await Post("/element", new Dictionary<string, string> { ["using"] = strategy, ["value"] = value });
        return result.GetProperty("value").GetProperty("element-6066-11e4-a52e-4f735466cecf").GetString()!;
    }

    [TestMethod]
    public async Task CreateSession_TestApp()
    {
        await CreateSession(TestAppPath);
        Assert.IsNotNull(_sessionId);

        var title = await Get("/title");
        Assert.IsTrue(title.GetProperty("value").GetString()!.Contains("Test App"));
    }

    [TestMethod]
    public async Task FindElement_ByClassName()
    {
        await CreateSession(TestAppPath);
        var elementId = await FindElement("class name", "Edit");
        Assert.IsFalse(string.IsNullOrEmpty(elementId));
    }

    [TestMethod]
    public async Task SendKeys_And_GetText()
    {
        await CreateSession(TestAppPath);
        var elementId = await FindElement("class name", "Edit");

        await Post($"/element/{elementId}/value", new { text = "Hello from WindowsDriverCore" });

        var textResult = await Get($"/element/{elementId}/text");
        Assert.IsTrue(textResult.GetProperty("value").GetString()!.Contains("Hello from WindowsDriverCore"));
    }

    [TestMethod]
    public async Task GetAttribute_NativeWindowHandle()
    {
        await CreateSession(TestAppPath);
        var elementId = await FindElement("class name", "Edit");

        var attrResult = await Get($"/element/{elementId}/attribute/NativeWindowHandle");
        var handle = attrResult.GetProperty("value").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(handle));
        Assert.IsTrue(long.Parse(handle!) > 0);
    }

    [TestMethod]
    public async Task Window_Size()
    {
        await CreateSession(TestAppPath);

        var rect = await Get("/window/rect");
        var value = rect.GetProperty("value");
        Assert.IsTrue(value.GetProperty("width").GetInt32() > 0);
        Assert.IsTrue(value.GetProperty("height").GetInt32() > 0);
    }

    [TestMethod]
    public async Task Window_Position()
    {
        await CreateSession(TestAppPath);

        var rect = await Get("/window/rect");
        var value = rect.GetProperty("value");
        Assert.IsTrue(value.TryGetProperty("x", out _));
        Assert.IsTrue(value.TryGetProperty("y", out _));
    }

    [TestMethod]
    public async Task Window_Maximize_Restore()
    {
        await CreateSession(TestAppPath);

        var originalRect = await Get("/window/rect");
        var origW = originalRect.GetProperty("value").GetProperty("width").GetInt32();
        var origH = originalRect.GetProperty("value").GetProperty("height").GetInt32();

        await Post("/window/maximize");
        var maxRect = await Get("/window/rect");
        var maxW = maxRect.GetProperty("value").GetProperty("width").GetInt32();
        var maxH = maxRect.GetProperty("value").GetProperty("height").GetInt32();
        Assert.IsTrue(maxW >= origW);
        Assert.IsTrue(maxH >= origH);

        await Post("/window/restore");
        var restRect = await Get("/window/rect");
        var restW = restRect.GetProperty("value").GetProperty("width").GetInt32();
        var restH = restRect.GetProperty("value").GetProperty("height").GetInt32();
        Assert.IsTrue(restW > 0);
        Assert.IsTrue(restH > 0);
    }

    [TestMethod]
    public async Task DeleteSession_TestApp()
    {
        await CreateSession(TestAppPath);
        Assert.IsNotNull(_sessionId);

        var response = await _http.DeleteAsync($"/session/{_sessionId}");
        response.EnsureSuccessStatusCode();
        _sessionId = null;
    }
}
