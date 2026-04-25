using System;

namespace Glasswork.Core.Models;

/// <summary>
/// Parses and builds <c>glasswork://</c> deep-link URIs.
///
/// Supported forms:
/// <list type="bullet">
///   <item><description><c>glasswork://task/&lt;id&gt;</c> — open the task detail view.</description></item>
///   <item><description><c>glasswork://my-day</c> — open My Day.</description></item>
///   <item><description><c>glasswork://backlog</c> — open Backlog.</description></item>
/// </list>
/// </summary>
public static class GlassworkUriParser
{
    /// <summary>The URI scheme registered by the app.</summary>
    public const string Scheme = "glasswork";

    /// <summary>
    /// Parse a <c>glasswork://</c> URI string into a <see cref="GlassworkUri"/>.
    /// Returns <c>null</c> for any input that is not a recognised Glasswork deep-link.
    /// </summary>
    public static GlassworkUri? Parse(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return null;
        if (!Uri.TryCreate(uriString.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var host = uri.Host.ToLowerInvariant();
        // AbsolutePath for glasswork://task/TASK-1 is "/TASK-1"; strip leading slash.
        var path = uri.AbsolutePath.Trim('/');

        switch (host)
        {
            case "task":
                if (string.IsNullOrEmpty(path)) return null;
                var taskId = Uri.UnescapeDataString(path);
                if (string.IsNullOrWhiteSpace(taskId)) return null;
                return new GlassworkUri.Task(taskId);

            case "my-day":
                return new GlassworkUri.MyDay();

            case "backlog":
                return new GlassworkUri.Backlog();

            default:
                return null;
        }
    }

    /// <summary>
    /// Build a <c>glasswork://</c> URI string for the given navigation target.
    /// </summary>
    public static string Build(GlassworkUri uri) => uri switch
    {
        GlassworkUri.Task t   => $"{Scheme}://task/{Uri.EscapeDataString(t.TaskId)}",
        GlassworkUri.MyDay    => $"{Scheme}://my-day",
        GlassworkUri.Backlog  => $"{Scheme}://backlog",
        _ => throw new ArgumentOutOfRangeException(nameof(uri), $"Unhandled GlassworkUri type: {uri.GetType().Name}")
    };
}
