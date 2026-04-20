namespace Glasswork.Core.Services;

/// <summary>
/// Pure resolution from a free-form parent string + optional ADO base URL to a
/// concrete URL the UI can open.
///
/// Rules (first match wins):
///   1. Parent starts with "http://" or "https://" → return as-is (trimmed).
///   2. Parent is purely numeric (after trim) AND baseUrl is non-empty
///      → "{trimmedBaseUrl}/_workitems/edit/{parent}".
///   3. Otherwise → null (caller should treat as "no link").
///
/// The base URL has any trailing slash trimmed so callers can paste either form.
/// </summary>
public static class AdoLinkResolver
{
    public static string? TryResolve(string? parent, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(parent)) return null;
        var p = parent.Trim();

        if (p.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
        {
            return p;
        }

        if (IsAllDigits(p) && !string.IsNullOrWhiteSpace(baseUrl))
        {
            var trimmedBase = baseUrl!.Trim().TrimEnd('/');
            if (trimmedBase.Length == 0) return null;
            return $"{trimmedBase}/_workitems/edit/{p}";
        }

        return null;
    }

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
