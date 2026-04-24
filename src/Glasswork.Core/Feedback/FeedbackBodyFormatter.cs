using System;
using System.Text;

namespace Glasswork.Core.Feedback;

/// <summary>
/// Builds the GitHub issue body submitted from the in-app feedback dialog.
/// Lives in Core so it can be unit-tested without spinning up WinUI.
/// </summary>
public static class FeedbackBodyFormatter
{
    /// <summary>
    /// Render the issue body. Layout is:
    /// <code>
    /// _Filed from Glasswork feedback dialog — category: **Bug**_
    ///
    /// ## Context
    /// | Field | Value |
    /// | --- | --- |
    /// | Page | MyDayPage |
    /// | Active task | task-... |
    /// | ...
    ///
    /// &lt;user body&gt;
    /// </code>
    /// When <paramref name="context"/> is null the Context section is omitted entirely
    /// (so the formatter degrades gracefully if the App can't capture context for some reason).
    /// </summary>
    public static string Build(string category, string? body, FeedbackContext? context)
    {
        var sb = new StringBuilder();
        sb.Append("_Filed from Glasswork feedback dialog — category: **")
          .Append(category)
          .Append("**_");

        if (context is not null)
        {
            sb.Append("\n\n## Context\n\n");
            sb.Append("| Field | Value |\n");
            sb.Append("| --- | --- |\n");
            AppendRow(sb, "Page", context.PageName);
            AppendRow(sb, "Active task", context.ActiveTaskId);
            AppendRow(sb, "App version", context.AppVersion);
            AppendRow(sb, "OS", context.OsDescription);
            AppendRow(sb, "Runtime", context.RuntimeVersion);
            AppendRow(sb, "Captured", context.CapturedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }

        var trimmedBody = body?.Trim() ?? string.Empty;
        if (trimmedBody.Length > 0)
        {
            sb.Append("\n\n");
            sb.Append(trimmedBody);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string field, string? value)
    {
        // Empty/null values render as "(none)" so a reviewer can tell the difference between
        // "we didn't capture this" and "the user wasn't on a task page" — the field is always present.
        var display = string.IsNullOrWhiteSpace(value) ? "(none)" : EscapeCell(value);
        sb.Append("| ").Append(field).Append(" | ").Append(display).Append(" |\n");
    }

    private static string EscapeCell(string s)
    {
        // Pipe and newline are the only chars that break GFM table rendering; escape pipes,
        // collapse newlines to spaces. Everything else (including underscores) is fine inline.
        return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    }
}
