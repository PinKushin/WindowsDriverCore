using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsDriverCore.Tests.Protocol.Recordings;

/// <summary>
/// One request/response pair captured from real WinAppDriver.
/// </summary>
public sealed record RecordedResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("requestBody")] string? RequestBody,
    [property: JsonPropertyName("httpStatus")] int HttpStatus,
    [property: JsonPropertyName("responseBody")] string? ResponseBody);

/// <summary>
/// Loads the checked-in recordings of real WinAppDriver responses.
/// </summary>
/// <remarks>
/// These are the protocol contract. They were captured by driving WinAppDriver
/// 1.2.2009.02003 against Calculator on Windows 11 26200 — not derived from the
/// specification, because reading the specification produced two confident and
/// wrong conclusions before this file existed.
/// </remarks>
public static class RecordedResponses
{
    private static readonly IReadOnlyList<RecordedResponse> AllRecords = Load();

    /// <summary>Every recorded request/response pair.</summary>
    public static IReadOnlyList<RecordedResponse> All => AllRecords;

    /// <summary>Looks up one recording by its name.</summary>
    /// <param name="name">The recording name, e.g. <c>error.noSuchElement.accessibilityId</c>.</param>
    /// <returns>The recording.</returns>
    /// <exception cref="KeyNotFoundException">No recording has that name.</exception>
    public static RecordedResponse Named(string name)
    {
        RecordedResponse? found = AllRecords.FirstOrDefault(r => r.Name == name);
        return found ?? throw new KeyNotFoundException(
            $"No recording named '{name}'. Available: {string.Join(", ", AllRecords.Select(r => r.Name))}");
    }

    private static List<RecordedResponse> Load()
    {
        string path = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Recordings", "winappdriver-responses.json");

        // utf-8 BOM: the recordings are written by PowerShell's Out-File -Encoding utf8.
        string json = File.ReadAllText(path).TrimStart('﻿');

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement records = document.RootElement.GetProperty("records");

        List<RecordedResponse> parsed = [];
        foreach (JsonElement element in records.EnumerateArray())
        {
            RecordedResponse? record = element.Deserialize<RecordedResponse>();
            if (record is not null)
            {
                parsed.Add(record);
            }
        }

        return parsed;
    }
}
