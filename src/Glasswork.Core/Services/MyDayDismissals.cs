using System;
using System.Globalization;

namespace Glasswork.Core.Services;

/// <summary>
/// Single source of truth for the "dismissed from My Day" ui-state key shape
/// (<c>dismissed.{yyyy-MM-dd}.{taskId}</c>) and for deciding when such a key is
/// stale. A dismissal only ever applies to the day it was created (see
/// <c>MyDayViewModel.IsDismissedToday</c>), so any key whose embedded date is
/// strictly before today is dead weight and can be garbage-collected at startup.
/// </summary>
public static class MyDayDismissals
{
    public const string Prefix = "dismissed.";

    /// <summary>Builds the ui-state key recording that <paramref name="taskId"/> was dismissed on <paramref name="date"/>.</summary>
    public static string KeyFor(string taskId, DateOnly date) =>
        $"{Prefix}{date:yyyy-MM-dd}.{taskId}";

    /// <summary>
    /// True only for a well-formed dismiss key whose embedded date is strictly
    /// before <paramref name="today"/>. Non-dismiss keys, malformed keys, today's
    /// keys, and (defensively) future-dated keys are all kept.
    /// </summary>
    public static bool IsStale(string key, DateOnly today)
    {
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var rest = key.Substring(Prefix.Length);
        var dot = rest.IndexOf('.');
        if (dot <= 0) return false;

        var datePart = rest.Substring(0, dot);
        if (!DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return false;

        return date < today;
    }
}
