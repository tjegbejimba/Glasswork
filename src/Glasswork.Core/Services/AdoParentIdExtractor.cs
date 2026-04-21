using System;

namespace Glasswork.Core.Services;

/// <summary>
/// Extracts the numeric Azure DevOps work-item ID from a "parent" string that
/// may be either a bare ID (e.g. <c>"37226063"</c>) or a full ADO URL
/// (e.g. <c>"https://dev.azure.com/org/proj/_workitems/edit/37226063"</c>).
/// Returns <c>null</c> for anything else (free text, blank, malformed URL).
/// Pure helper — safe to call from any thread.
/// </summary>
public static class AdoParentIdExtractor
{
    public static int? TryExtractId(string? parent)
    {
        if (string.IsNullOrWhiteSpace(parent)) return null;
        var trimmed = parent.Trim();

        if (IsAllDigits(trimmed))
        {
            return int.TryParse(trimmed, out var id) && id > 0 ? id : null;
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Find /edit/{digits} segment, ignoring any trailing slash, query or fragment.
            const string Marker = "/_workitems/edit/";
            var markerIdx = trimmed.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (markerIdx < 0) return null;

            var start = markerIdx + Marker.Length;
            var end = start;
            while (end < trimmed.Length && trimmed[end] >= '0' && trimmed[end] <= '9')
            {
                end++;
            }
            if (end == start) return null;

            // Anything immediately after the digits must be a terminator (end of string,
            // '/', '?', or '#') — guards against e.g. /edit/12abc which shouldn't match.
            if (end < trimmed.Length)
            {
                var c = trimmed[end];
                if (c != '/' && c != '?' && c != '#') return null;
            }

            return int.TryParse(trimmed[start..end], out var id) && id > 0 ? id : null;
        }

        return null;
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }
}
