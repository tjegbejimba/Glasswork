using System.Text;
using System.Text.Json;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed class ChildActivitySummaryService
{
    public const string Filename = "child-activity-summary.md";

    private readonly string _taskDirectory;
    private readonly ResourceMutationService _mutations;
    private readonly FrontmatterParser _parser = new();

    public ChildActivitySummaryService(
        string taskDirectory,
        VaultService vault,
        ResourceMutationService mutations)
    {
        _taskDirectory = Path.GetFullPath(
            taskDirectory ?? throw new ArgumentNullException(nameof(taskDirectory)));
        ArgumentNullException.ThrowIfNull(vault);
        if (!string.Equals(
                _taskDirectory.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(vault.VaultPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Task directory must match the VaultService path.",
                nameof(taskDirectory));
        }
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
    }

    public ChildActivitySummaryCapture Capture(string parentId)
    {
        if (string.IsNullOrWhiteSpace(parentId))
            throw new ArgumentException("Parent Task ID is required.", nameof(parentId));

        using var lease = VaultScopedCoordinator.EnterShared(_taskDirectory);
        return CaptureSnapshot(parentId, ReadTasksUnsafe());
    }

    public ResourceMutationOutcome Commit(
        ChildActivitySummaryCapture capture,
        string generatedBody,
        DateTimeOffset generatedAt,
        string mutationId)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (generatedBody is null)
            throw new ArgumentNullException(nameof(generatedBody));
        if (string.IsNullOrWhiteSpace(mutationId))
            throw new ArgumentException("Mutation ID is required.", nameof(mutationId));

        var bytes = Encoding.UTF8.GetBytes(Serialize(capture, generatedBody, generatedAt));
        var preconditionHash = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                capture.ParentId,
                capture.ParentRevision,
                ReadBasis = capture.ReadBasis.OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal),
            }));

        return _mutations.CommitTaskOwnedFileConditionalWithPrecondition(
            SummaryPath(capture.ParentId),
            bytes,
            overwrite: capture.ExpectedSummaryRevision is not null,
            mutationId,
            capture.ExpectedSummaryRevision,
            ifAbsent: capture.ExpectedSummaryRevision is null ? true : null,
            preconditionHash,
            () => ValidateBasis(capture));
    }

    public ChildActivitySummaryState ReadState(string parentId)
    {
        if (string.IsNullOrWhiteSpace(parentId))
            throw new ArgumentException("Parent Task ID is required.", nameof(parentId));

        return ReadStates([parentId])[parentId];
    }

    /// <summary>
    /// Reads summary freshness for several Parent Tasks from one coherent Vault snapshot.
    /// This avoids reparsing the entire task directory once per visible Parent row.
    /// </summary>
    public IReadOnlyDictionary<string, ChildActivitySummaryState> ReadStates(
        IEnumerable<string> parentIds)
    {
        ArgumentNullException.ThrowIfNull(parentIds);
        var ids = parentIds
            .Select(parentId => parentId?.Trim())
            .Where(parentId => !string.IsNullOrEmpty(parentId))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, ChildActivitySummaryState>(StringComparer.Ordinal);

        using var lease = VaultScopedCoordinator.EnterShared(_taskDirectory);
        IReadOnlyList<GlassworkTask> tasks;
        try
        {
            tasks = ReadTasksUnsafe();
        }
        catch (Exception exception) when (
            ResourceMutationService.IsExpectedPersistenceFailure(exception))
        {
            return ids.ToDictionary(
                id => id,
                _ => new ChildActivitySummaryState(
                    ChildActivitySummaryStateKind.Failed,
                    Error: $"Child activity summary inputs could not be read: {exception.Message}"),
                StringComparer.Ordinal);
        }

        return ids.ToDictionary(
            id => id,
            id => ReadState(id, tasks),
            StringComparer.Ordinal);
    }

    private ChildActivitySummaryState ReadState(
        string parentId,
        IReadOnlyList<GlassworkTask> tasks)
    {
        ChildActivitySummaryCapture capture;
        try
        {
            capture = CaptureSnapshot(parentId, tasks);
        }
        catch (ChildActivitySummaryException exception)
        {
            return new(ChildActivitySummaryStateKind.Failed, Error: exception.Message);
        }
        catch (Exception exception) when (
            ResourceMutationService.IsExpectedPersistenceFailure(exception))
        {
            return new(
                ChildActivitySummaryStateKind.Failed,
                Error: $"Child activity summary inputs could not be read: {exception.Message}");
        }

        var path = SummaryPath(parentId);
        if (!File.Exists(path))
            return new(ChildActivitySummaryStateKind.Missing);

        try
        {
            var bytes = File.ReadAllBytes(path);
            var parsed = Parse(Encoding.UTF8.GetString(bytes));
            var kind = BasisEquals(parsed.ReadBasis!, capture.ReadBasis)
                ? ChildActivitySummaryStateKind.Current
                : ChildActivitySummaryStateKind.OutOfDate;
            return parsed with
            {
                Kind = kind,
                ResourceRevision = ResourceMutationService.Revision(bytes),
            };
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException
            || ResourceMutationService.IsExpectedPersistenceFailure(exception))
        {
            return new(
                ChildActivitySummaryStateKind.Failed,
                Error: $"Child activity summary metadata is invalid: {exception.Message}");
        }
    }

    private ChildActivitySummaryCapture CaptureSnapshot(
        string parentId,
        IReadOnlyList<GlassworkTask> tasks)
    {
        var parent = tasks.FirstOrDefault(task =>
            string.Equals(task.Id, parentId, StringComparison.Ordinal));
        if (parent is null)
            throw new ChildActivitySummaryException(
                "parent_not_found",
                $"Parent Task '{parentId}' was not found.");
        if (!GlassworkTask.Types.IsParent(parent.Type))
            throw new ChildActivitySummaryException(
                "not_parent",
                $"Task '{parentId}' is not a Parent Task.");

        var hierarchy = new TaskHierarchyPolicy(tasks);
        var descendants = hierarchy.GetDescendants(parent.Id);
        var diagnostics = hierarchy.Validate(
            descendants.Select(task => task.Id).Prepend(parent.Id));
        if (diagnostics.Count > 0)
            throw new ChildActivitySummaryException(
                diagnostics[0].Code,
                diagnostics[0].Message);

        var inputs = descendants.ToDictionary(
            task => task.Id,
            CreateTaskInput,
            StringComparer.Ordinal);
        var groups = hierarchy.GetChildren(parent.Id)
            .Select(child => new ChildActivitySummaryGroup(
                inputs[child.Id],
                DescendantBranch(child, hierarchy)
                    .Select(task => inputs[task.Id])
                    .ToArray()))
            .ToArray();
        var basis = BuildBasis(inputs.Values);
        var summaryPath = SummaryPath(parent.Id);
        var summaryBytes = File.Exists(summaryPath)
            ? File.ReadAllBytes(summaryPath)
            : null;

        return new ChildActivitySummaryCapture(
            parent.Id,
            parent.ResourceRevision
                ?? throw new ChildActivitySummaryException(
                    "missing_revision",
                    $"Parent Task '{parent.Id}' has no Resource Revision."),
            descendants.Count,
            groups,
            basis,
            summaryBytes is null ? null : ResourceMutationService.Revision(summaryBytes));
    }

    private string? ValidateBasis(ChildActivitySummaryCapture expected)
    {
        try
        {
            var current = CaptureSnapshot(expected.ParentId, ReadTasksUnsafe());
            if (!string.Equals(
                    expected.ParentRevision,
                    current.ParentRevision,
                    StringComparison.Ordinal))
            {
                return $"Parent Task '{expected.ParentId}' changed before summary commit.";
            }
            foreach (var resource in expected.ReadBasis)
            {
                if (!current.ReadBasis.TryGetValue(resource.Key, out var revision))
                    return MissingResourceMessage(resource.Key);
                if (!string.Equals(resource.Value, revision, StringComparison.Ordinal))
                    return ChangedResourceMessage(resource.Key);
            }

            var added = current.ReadBasis.Keys
                .FirstOrDefault(key => !expected.ReadBasis.ContainsKey(key));
            if (added is not null)
                return $"The descendant tree changed before summary commit: '{added}' was added.";
            return expected.DescendantCount == current.DescendantCount
                ? null
                : $"The descendant count changed from {expected.DescendantCount} " +
                    $"to {current.DescendantCount} before summary commit.";
        }
        catch (ChildActivitySummaryException exception)
        {
            return exception.Message;
        }
    }

    private ChildActivitySummaryTaskInput CreateTaskInput(GlassworkTask task) =>
        new(
            task.Id,
            task.Title,
            task.Status,
            task.Type,
            task.SourceKind,
            task.ResourceRevision
                ?? throw new ChildActivitySummaryException(
                    "missing_revision",
                    $"Task '{task.Id}' has no Resource Revision."),
            BoundedText(task.Notes),
            task.Links.Select(link => new TaskLink
            {
                Type = link.Type,
                Value = link.Value,
                Label = link.Label,
            }).ToArray(),
            LoadArtifacts(task.Id));

    private IReadOnlyList<ChildActivitySummaryArtifactInput> LoadArtifacts(string taskId)
    {
        var folder = Path.Combine(_taskDirectory, taskId + ".artifacts");
        if (!Directory.Exists(folder))
            return [];

        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(ArtifactCommitPolicy.IsCommitted)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                var kind = ArtifactKindResolver.Resolve(path);
                var filename = Path.GetFileName(path);
                var inline = (kind is ArtifactKind.Markdown or ArtifactKind.Text)
                    && bytes.LongLength <= ArtifactCaps.InlineTextBytes;
                return new ChildActivitySummaryArtifactInput(
                    filename,
                    kind,
                    bytes.LongLength,
                    ResourceMutationService.Revision(bytes),
                    inline ? Encoding.UTF8.GetString(bytes) : null,
                    string.Equals(filename, Filename, StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
    }

    private IReadOnlyList<GlassworkTask> ReadTasksUnsafe()
    {
        var tasks = new List<GlassworkTask>();
        foreach (var path in Directory.EnumerateFiles(
                     _taskDirectory,
                     "*.md",
                     SearchOption.TopDirectoryOnly)
                 .Where(path => !Path.GetFileName(path).StartsWith('_'))
                 .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var task = _parser.Parse(Encoding.UTF8.GetString(bytes));
                task.ResourceRevision = ResourceMutationService.Revision(bytes);
                tasks.Add(task);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Child activity summary skipped malformed Task '{path}': {exception.Message}");
            }
        }
        return tasks;
    }

    private static IReadOnlyDictionary<string, string> BuildBasis(
        IEnumerable<ChildActivitySummaryTaskInput> tasks)
    {
        var basis = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var task in tasks.OrderBy(task => task.Id, StringComparer.Ordinal))
        {
            basis[$"task:{task.Id}"] = task.ResourceRevision;
            foreach (var artifact in task.Artifacts)
                basis[$"artifact:{task.Id}/{artifact.Filename}"] = artifact.ResourceRevision;
        }
        return basis;
    }

    private static IReadOnlyList<GlassworkTask> DescendantBranch(
        GlassworkTask directChild,
        TaskHierarchyPolicy hierarchy)
    {
        var branch = new List<GlassworkTask> { directChild };

        void Visit(string taskId)
        {
            foreach (var child in hierarchy.GetChildren(taskId))
            {
                branch.Add(child);
                Visit(child.Id);
            }
        }

        Visit(directChild.Id);
        return branch;
    }

    private static string BoundedText(string value) =>
        Encoding.UTF8.GetByteCount(value) <= ArtifactCaps.InlineTextBytes
            ? value
            : Encoding.UTF8.GetString(
                Encoding.UTF8.GetBytes(value),
                0,
                (int)ArtifactCaps.InlineTextBytes);

    private static string Serialize(
        ChildActivitySummaryCapture capture,
        string body,
        DateTimeOffset generatedAt)
    {
        var basisJson = JsonSerializer.SerializeToUtf8Bytes(capture.ReadBasis);
        var encodedBasis = Convert.ToBase64String(basisJson);
        return $"""
            ---
            title: Child activity summary
            glasswork_kind: child_activity_summary
            schema_version: 1
            generated_at: {generatedAt:O}
            descendant_count: {capture.DescendantCount}
            read_basis_json: {encodedBasis}
            ---

            {body.Trim()}
            """;
    }

    private static ChildActivitySummaryState Parse(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length < 8 || lines[0] != "---")
            throw new FormatException("The metadata frontmatter is missing.");

        var close = Array.IndexOf(lines, "---", 1);
        if (close < 0)
            throw new FormatException("The metadata frontmatter is not closed.");

        var metadata = lines[1..close]
            .Select(line => line.Split(": ", 2, StringSplitOptions.None))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (!metadata.TryGetValue("glasswork_kind", out var kind)
            || kind != "child_activity_summary"
            || !metadata.TryGetValue("schema_version", out var schema)
            || schema != "1")
        {
            throw new FormatException("The metadata kind or schema version is unsupported.");
        }
        if (!metadata.TryGetValue("generated_at", out var generatedRaw)
            || !DateTimeOffset.TryParse(
                generatedRaw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var generatedAt))
        {
            throw new FormatException("The generated time is invalid.");
        }
        if (!metadata.TryGetValue("descendant_count", out var countRaw)
            || !int.TryParse(
                countRaw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var descendantCount)
            || descendantCount < 0)
        {
            throw new FormatException("The descendant count is invalid.");
        }
        if (!metadata.TryGetValue("read_basis_json", out var encodedBasis))
            throw new FormatException("The Resource Revision read basis is missing.");

        Dictionary<string, string>? basis;
        try
        {
            basis = JsonSerializer.Deserialize<Dictionary<string, string>>(
                Convert.FromBase64String(encodedBasis));
        }
        catch (FormatException exception)
        {
            throw new FormatException("The Resource Revision read basis is invalid.", exception);
        }
        if (basis is null)
            throw new FormatException("The Resource Revision read basis is invalid.");

        var body = string.Join('\n', lines[(close + 1)..]).Trim();
        return new(
            ChildActivitySummaryStateKind.Current,
            body,
            generatedAt,
            descendantCount,
            basis);
    }

    private static bool BasisEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair =>
            right.TryGetValue(pair.Key, out var revision)
            && string.Equals(pair.Value, revision, StringComparison.Ordinal));

    private static string MissingResourceMessage(string key) =>
        key.StartsWith("task:", StringComparison.Ordinal)
            ? $"Task '{key["task:".Length..]}' is missing from the descendant tree."
            : $"Artifact '{key["artifact:".Length..]}' is missing from the descendant tree.";

    private static string ChangedResourceMessage(string key) =>
        key.StartsWith("task:", StringComparison.Ordinal)
            ? $"Task '{key["task:".Length..]}' changed before summary commit."
            : $"Artifact '{key["artifact:".Length..]}' changed before summary commit.";

    private string SummaryPath(string parentId) =>
        Path.Combine(_taskDirectory, parentId + ".artifacts", Filename);
}
