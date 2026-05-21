using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Stateless task search over vault task content.
/// </summary>
public sealed class TaskSearchService
{
    private static readonly HashSet<string> ValidFields = new(StringComparer.Ordinal)
    {
        "title",
        "description",
        "notes",
        "subtasks",
        "tags",
    };

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
        int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query is required.");
        if (query.Length > 500)
            throw new ArgumentException("query must be 500 characters or fewer.");

        var clampedLimit = Math.Clamp(limit, 1, 100);
        var scope = NormalizeScope(fields);
        var requiredTagSet = NormalizeTags(requiredTags);
        var allowedStatuses = NormalizeStatuses(statuses);
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var all = _vault.LoadAll();
        var hits = new List<ScoredHit>();

        foreach (var task in all)
        {
            if (allowedStatuses is not null && !allowedStatuses.Contains(task.Status))
                continue;

            if (requiredTagSet is not null)
            {
                var taskTags = new HashSet<string>(task.Tags, StringComparer.OrdinalIgnoreCase);
                if (!requiredTagSet.All(taskTags.Contains))
                    continue;
            }

            var searchable = BuildSearchableFields(task, scope);
            if (!AllTokensMatch(tokens, searchable))
                continue;

            var matchedFields = new List<string>();
            foreach (var pair in searchable)
            {
                if (tokens.Any(t => pair.Value.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    matchedFields.Add(pair.Key);
            }

            if (matchedFields.Count == 0)
                continue;

            var snippet = BuildSnippet(searchable, matchedFields, tokens);
            var score = ComputeScore(matchedFields);
            var effectiveStatus = task.Status == GlassworkTask.Statuses.InProgress ? "doing" : task.Status;
            hits.Add(new ScoredHit(
                new TaskSearchHit(
                    task.Id,
                    task.Title,
                    effectiveStatus,
                    task.Parent,
                    matchedFields,
                    snippet),
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

    private static Dictionary<string, string> BuildSearchableFields(GlassworkTask task, HashSet<string> scope)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (scope.Contains("title")) result["title"] = task.Title ?? string.Empty;
        if (scope.Contains("description")) result["description"] = task.Description ?? string.Empty;
        if (scope.Contains("notes")) result["notes"] = task.Notes ?? string.Empty;
        if (scope.Contains("subtasks"))
        {
            result["subtasks"] = string.Join(
                "\n",
                task.Subtasks.Select(s => $"{s.Text}\n{s.Notes}".Trim()));
        }

        if (scope.Contains("tags")) result["tags"] = string.Join(" ", task.Tags);
        return result;
    }

    private static HashSet<string> NormalizeScope(IReadOnlyList<string>? fields)
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

    private static HashSet<string>? NormalizeTags(IReadOnlyList<string>? tags)
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

    private static HashSet<string>? NormalizeStatuses(IReadOnlyList<string>? statuses)
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
                "done" => GlassworkTask.Statuses.Done,
                _ => throw new ArgumentException($"Invalid status '{raw}'. Valid values: todo, doing, done.")
            });
        }

        return set;
    }

    private static bool AllTokensMatch(IReadOnlyList<string> tokens, Dictionary<string, string> searchable)
    {
        foreach (var token in tokens)
        {
            if (!searchable.Values.Any(v => v.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    private static string BuildSnippet(
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

    private static int ComputeScore(IReadOnlyCollection<string> matchedFields)
    {
        var score = matchedFields.Count;
        if (matchedFields.Contains("title"))
            score += 10;
        return score;
    }

    private sealed record ScoredHit(TaskSearchHit Hit, int Score, DateTime Created);
}

public sealed record TaskSearchHit(
    string Id,
    string Title,
    string Status,
    string? ParentId,
    IReadOnlyList<string> MatchedIn,
    string Snippet);
