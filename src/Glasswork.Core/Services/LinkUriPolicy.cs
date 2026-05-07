using System;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Deep module that owns "given a TaskLink, where do I navigate."
/// Returns null for malformed entries. Adding a future link type = one localized branch + tests.
/// </summary>
public static class LinkUriPolicy
{
    public static Uri? Resolve(TaskLink link, string? adoBaseUrl)
    {
        if (link is null) return null;

        switch (link.Type)
        {
            case TaskLink.Types.Ado:
                return ResolveAdo(link.Value, adoBaseUrl);
            case TaskLink.Types.Pr:
                return ResolvePr(link.Value, adoBaseUrl);
            case TaskLink.Types.Incident:
                return ResolveIncident(link.Value);
            case TaskLink.Types.Doc:
            case TaskLink.Types.Build:
            case TaskLink.Types.Other:
                return ResolveUrl(link.Value);
            default:
                // Unknown types treated as "other" - URL pass-through
                return ResolveUrl(link.Value);
        }
    }

    public static string DisplayText(TaskLink link)
    {
        if (link is null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(link.Label)) return link.Label;

        return link.Type switch
        {
            TaskLink.Types.Ado => $"ADO #{link.Value}",
            TaskLink.Types.Incident => FormatIncidentDisplay(link.Value),
            TaskLink.Types.Pr => FormatPrDisplay(link.Value),
            TaskLink.Types.Doc => FormatHostDisplay(link.Value, "Doc"),
            TaskLink.Types.Build => FormatHostDisplay(link.Value, "Build"),
            TaskLink.Types.Other => FormatHostDisplay(link.Value, null),
            _ => FormatHostDisplay(link.Value, null)
        };
    }

    // ── Type-specific resolution ────────────────────────────────────────────

    private static Uri? ResolveAdo(string? value, string? adoBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.IsNullOrWhiteSpace(adoBaseUrl)) return null;

        var trimmedBase = adoBaseUrl.Trim().TrimEnd('/');
        if (trimmedBase.Length == 0) return null;

        var url = $"{trimmedBase}/_workitems/edit/{value.Trim()}";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static Uri? ResolvePr(string? value, string? adoBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();

        // If it's a URL, pass through
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri : null;
        }

        // If it's an integer and we have ADO base URL, build ADO PR URL
        if (IsAllDigits(trimmed) && !string.IsNullOrWhiteSpace(adoBaseUrl))
        {
            var trimmedBase = adoBaseUrl.Trim().TrimEnd('/');
            if (trimmedBase.Length == 0) return null;
            var url = $"{trimmedBase}/_git/pullrequest/{trimmed}";
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
        }

        return null;
    }

    private static Uri? ResolveIncident(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();

        // If it's a URL, pass through
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri : null;
        }

        // Extract incident number from "ICM 123456" or bare "123456"
        // Anchor to require entire string matches pattern (not just substring)
        var match = Regex.Match(trimmed, @"^(?:ICM\s+)?(\d+)$", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups[1].Success)
        {
            var incidentId = match.Groups[1].Value;
            var url = $"https://portal.microsofticm.com/imp/v5/incidents/details/{incidentId}/home";
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
        }

        return null;
    }

    private static Uri? ResolveUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri : null;
        }

        return null;
    }

    // ── Display text formatting ─────────────────────────────────────────────

    private static string FormatIncidentDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();

        // If already has ICM prefix, return as-is
        if (trimmed.StartsWith("ICM ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // If it's a bare number, add ICM prefix
        if (IsAllDigits(trimmed))
        {
            return $"ICM {trimmed}";
        }

        // Otherwise (e.g., URL), return as-is
        return trimmed;
    }

    private static string FormatPrDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();

        // If it's a number, format as "PR #123"
        if (IsAllDigits(trimmed))
        {
            return $"PR #{trimmed}";
        }

        // If it's a URL, extract host
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return $"PR ({uri.Host})";
        }

        return trimmed;
    }

    private static string FormatHostDisplay(string? value, string? typeLabel)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return typeLabel is null
                ? uri.Host
                : $"{typeLabel} ({uri.Host})";
        }

        return trimmed;
    }

    // ── Utilities ───────────────────────────────────────────────────────────

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }
}
