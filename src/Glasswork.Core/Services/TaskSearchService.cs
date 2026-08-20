using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Stateless task search over vault task content.
/// </summary>
public sealed class TaskSearchService
{
    private readonly VaultService _vault;

    public TaskSearchService(VaultService vault)
    {
        _vault = vault;
    }

    public IReadOnlyList<TaskSearchHit> Search(
        string query,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<string>? requiredTags = null,
        IReadOnlyList<string>? statuses = null,
        int limit = 20,
        string? size = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query is required.");
        if (query.Length > 500)
            throw new ArgumentException("query must be 500 characters or fewer.");

        var clampedLimit = Math.Clamp(limit, 1, 100);
        var scope = TaskSearchText.NormalizeScope(fields);
        var requiredTagSet = TaskSearchText.NormalizeTags(requiredTags);
        var allowedStatuses = TaskSearchText.NormalizeStatuses(statuses);
        var tokens = TaskSearchText.Tokenize(query);

        var documents = _vault.LoadAll()
            .Select(TaskSearchDocument.FromTask)
            .ToArray();
        return Search(documents, query, fields, requiredTags, statuses, limit, size);
    }

    public IReadOnlyList<TaskSearchHit> Search(
        IReadOnlyList<TaskSearchDocument> documents,
        string query,
        IReadOnlyList<string>? fields = null,
        IReadOnlyList<string>? requiredTags = null,
        IReadOnlyList<string>? statuses = null,
        int limit = 20,
        string? size = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query is required.");
        if (query.Length > 500)
            throw new ArgumentException("query must be 500 characters or fewer.");

        var clampedLimit = Math.Clamp(limit, 1, 100);
        var scope = TaskSearchText.NormalizeScope(fields);
        var requiredTagSet = TaskSearchText.NormalizeTags(requiredTags);
        var allowedStatuses = TaskSearchText.NormalizeStatuses(statuses);
        var explicitSize = NormalizeSizeFilter(size);
        var tokens = TaskSearchText.Tokenize(query);
        var hits = new List<ScoredHit>();

        foreach (var task in documents)
        {
            if (allowedStatuses is null && task.Status == GlassworkTask.Statuses.Cancelled)
                continue;
            if (allowedStatuses is not null && !allowedStatuses.Contains(task.Status))
                continue;
            if (explicitSize.HasValue && task.ExplicitSize != explicitSize)
                continue;

            if (requiredTagSet is not null)
            {
                var taskTags = new HashSet<string>(task.Tags, StringComparer.OrdinalIgnoreCase);
                if (!requiredTagSet.All(taskTags.Contains))
                    continue;
            }

            var searchable = TaskSearchText.BuildSearchableFields(task, scope);
            if (!TaskSearchText.AllTokensMatch(tokens, searchable))
                continue;

            var matchedFields = TaskSearchText.MatchedFields(searchable, tokens);

            if (matchedFields.Count == 0)
                continue;

            var snippet = TaskSearchText.BuildSnippet(searchable, matchedFields, tokens);
            var score = TaskSearchText.ComputeScore(matchedFields);
            var effectiveStatus = task.Status == GlassworkTask.Statuses.InProgress ? "doing" : task.Status;
            hits.Add(new ScoredHit(
                new TaskSearchHit(
                    task.Id,
                    task.Title,
                    effectiveStatus,
                    task.ParentId,
                    matchedFields,
                    snippet,
                    task.Size),
                score,
                task.Created));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Created)
            .ThenBy(h => h.Hit.Id, StringComparer.Ordinal)
            .Take(clampedLimit)
            .Select(h => h.Hit)
            .ToArray();
    }

    private sealed record ScoredHit(TaskSearchHit Hit, int Score, DateTime Created);

    private static SizeBucket? NormalizeSizeFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (SizeBuckets.TryParse(raw, out var bucket))
            return bucket;
        throw new ArgumentException(
            $"Invalid size '{raw}'. Valid values: quick, short, focus, deep, break_down.");
    }
}

internal static class TaskSearchText
{
    private static readonly HashSet<string> ValidFields = new(StringComparer.Ordinal)
    {
        "title",
        "description",
        "notes",
        "subtasks",
        "tags",
    };

    internal static bool Matches(GlassworkTask task, string? query, IReadOnlyList<string>? fields = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var scope = NormalizeScope(fields);
        var tokens = Tokenize(query);
        var searchable = BuildSearchableFields(task, scope);
        return AllTokensMatch(tokens, searchable);
    }

    internal static string[] Tokenize(string query)
    {
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static Dictionary<string, string> BuildSearchableFields(GlassworkTask task, HashSet<string> scope)
        => BuildSearchableFields(TaskSearchDocument.FromTask(task), scope);

    internal static Dictionary<string, string> BuildSearchableFields(
        TaskSearchDocument task,
        HashSet<string> scope)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (scope.Contains("title")) result["title"] = task.Title ?? string.Empty;
        if (scope.Contains("description")) result["description"] = task.Description ?? string.Empty;
        if (scope.Contains("notes")) result["notes"] = task.Notes ?? string.Empty;
        if (scope.Contains("subtasks"))
        {
            result["subtasks"] = string.Join("\n", task.Subtasks);
        }

        if (scope.Contains("tags")) result["tags"] = string.Join(" ", task.Tags);
        return result;
    }

    internal static HashSet<string> NormalizeScope(IReadOnlyList<string>? fields)
    {
        if (fields is null || fields.Count == 0)
            return new HashSet<string>(ValidFields, StringComparer.Ordinal);

        var scope = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in fields)
        {
            var field = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (!ValidFields.Contains(field))
                throw new ArgumentException($"Invalid in field '{raw}'. Valid values: {string.Join(", ", ValidFields)}.");
            scope.Add(field);
        }

        return scope;
    }

    internal static HashSet<string>? NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
            return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
                set.Add(tag.Trim());
        }

        return set.Count == 0 ? null : set;
    }

    internal static HashSet<string>? NormalizeStatuses(IReadOnlyList<string>? statuses)
    {
        if (statuses is null || statuses.Count == 0)
            return null;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in statuses)
        {
            var status = (raw ?? string.Empty).Trim().ToLowerInvariant();
            set.Add(status switch
            {
                "todo" => GlassworkTask.Statuses.Todo,
                "doing" => GlassworkTask.Statuses.InProgress,
                "in-progress" => GlassworkTask.Statuses.InProgress,
                "blocked" => GlassworkTask.Statuses.Blocked,
                "done" => GlassworkTask.Statuses.Done,
                "cancelled" => GlassworkTask.Statuses.Cancelled,
                _ => throw new ArgumentException(
                    $"Invalid status '{raw}'. Valid values: todo, doing, blocked, done, cancelled.")
            });
        }

        return set;
    }

    internal static bool AllTokensMatch(IReadOnlyList<string> tokens, Dictionary<string, string> searchable)
    {
        foreach (var token in tokens)
        {
            if (!searchable.Values.Any(v => v.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    internal static List<string> MatchedFields(
        Dictionary<string, string> searchable,
        IReadOnlyList<string> tokens)
    {
        var matchedFields = new List<string>();
        foreach (var pair in searchable)
        {
            if (tokens.Any(t => pair.Value.Contains(t, StringComparison.OrdinalIgnoreCase)))
                matchedFields.Add(pair.Key);
        }
        return matchedFields;
    }

    internal static string BuildSnippet(
        Dictionary<string, string> searchable,
        IReadOnlyList<string> matchedFields,
        IReadOnlyList<string> tokens)
    {
        var priority = new[] { "title", "description", "notes", "subtasks", "tags" };
        var sourceField = priority.FirstOrDefault(matchedFields.Contains) ?? matchedFields[0];
        var source = searchable[sourceField];
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        var firstToken = tokens[0];
        var idx = source.IndexOf(firstToken, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = 0;

        const int targetLen = 120;
        var start = Math.Max(0, idx - 40);
        var len = Math.Min(targetLen, source.Length - start);
        var segment = source.Substring(start, len).Replace("\r", " ").Replace("\n", " ").Trim();

        if (start > 0) segment = "..." + segment;
        if (start + len < source.Length) segment += "...";
        return segment;
    }

    internal static int ComputeScore(IReadOnlyCollection<string> matchedFields)
    {
        var score = matchedFields.Count;
        if (matchedFields.Contains("title"))
            score += 10;
        return score;
    }
}

public sealed record TaskSearchHit(
    string Id,
    string Title,
    string Status,
    string? ParentId,
    IReadOnlyList<string> MatchedIn,
    string Snippet,
    string? Size = null);

public sealed record TaskSearchDocument(
    string Id,
    string Title,
    string Status,
    string? ParentId,
    DateTime Created,
    string Description,
    string Notes,
    IReadOnlyList<string> Subtasks,
    IReadOnlyList<string> Tags,
    string? Size = null)
{
    internal SizeBucket? ExplicitSize =>
        SizeBuckets.TryParse(Size, out var bucket) ? bucket : null;

    internal static TaskSearchDocument FromTask(GlassworkTask task) =>
        new(
            task.Id,
            task.Title,
            task.Status,
            task.Parent,
            task.Created,
            task.Description,
            task.Notes,
            task.Subtasks.Select(subtask => $"{subtask.Text}\n{subtask.Notes}".Trim()).ToArray(),
            task.Tags.ToArray(),
            task.Size);
}
