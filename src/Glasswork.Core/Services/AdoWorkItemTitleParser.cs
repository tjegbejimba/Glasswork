using System;
using System.Text.Json;

namespace Glasswork.Core.Services;

/// <summary>
/// Pure: parses the JSON output of <c>az boards work-item show -o json</c> and returns
/// the work item's <c>System.Title</c>, or null on any failure.
///
/// Tolerant of leading non-JSON lines (e.g. <c>az</c>'s upgrade WARNINGs that occasionally
/// leak into stdout despite being a stderr concern) by skipping ahead to the first
/// <c>{</c> or <c>[</c> character before parsing.
/// </summary>
public static class AdoWorkItemTitleParser
{
    public static string? TryParseTitle(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        // Skip leading lines that aren't part of the JSON document. az writes its
        // upgrade warning to stderr, but defensive parsing here costs nothing.
        var startIdx = json!.IndexOfAny(['{', '[']);
        if (startIdx < 0) return null;
        var payload = json[startIdx..];

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("fields", out var fields)) return null;
            if (!fields.TryGetProperty("System.Title", out var title)) return null;
            if (title.ValueKind != JsonValueKind.String) return null;
            var s = title.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            return s.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
