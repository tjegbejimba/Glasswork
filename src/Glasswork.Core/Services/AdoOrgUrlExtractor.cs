using System;

namespace Glasswork.Core.Services;

/// <summary>
/// Pure: extracts the Azure DevOps organization URL (suitable for <c>az --org</c>) from a
/// user-configured base URL.
///
/// Recognized forms:
///   • <c>https://{org}.visualstudio.com[/{project}]</c> → <c>https://{org}.visualstudio.com</c>
///   • <c>https://dev.azure.com/{org}[/{project}]</c>   → <c>https://dev.azure.com/{org}</c>
///
/// Returns null for empty/whitespace input, non-URL strings, and dev.azure.com URLs that
/// don't carry an organization segment.
/// </summary>
public static class AdoOrgUrlExtractor
{
    public static string? TryExtract(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var trimmed = baseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host;
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // {org}.visualstudio.com → org is implicit in the host; return host only.
        if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{host}";
        }

        // dev.azure.com/{org}[/{project}] → keep first path segment.
        if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length == 0) return null;
            return $"{uri.Scheme}://{host}/{segments[0]}";
        }

        return null;
    }
}
