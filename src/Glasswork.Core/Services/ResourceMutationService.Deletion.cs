using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Glasswork.Core.Markdown;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public sealed record TaskDeletionTask(
    string Id,
    string Title,
    string ResourceRevision);

public sealed record TaskDeletionArtifact(
    string TaskId,
    string VaultRelativePath);

public sealed record TaskDeletionBacklinkPage(
    string VaultRelativePath,
    int ReplacementCount);

public sealed record TaskDeletionPreflight(
    TaskDeletionTask Task,
    IReadOnlyList<TaskDeletionTask> Descendants,
    IReadOnlyList<TaskDeletionArtifact> Artifacts,
    IReadOnlyList<TaskDeletionBacklinkPage> BacklinkPages,
    IReadOnlyList<string> ArtifactDirectories,
    string PreflightRevision);

public sealed record TaskDeletionPreflightOutcome(
    string Outcome,
    TaskDeletionPreflight? Preflight = null,
    string? Error = null,
    IReadOnlyList<ResourceMutationDiagnostic>? Diagnostics = null);

public sealed record TaskDeletionReport(
    IReadOnlyList<TaskDeletionTask> DeletedTasks,
    IReadOnlyList<TaskDeletionTask> Descendants,
    IReadOnlyList<TaskDeletionArtifact> RemovedArtifacts,
    IReadOnlyList<TaskDeletionBacklinkPage> RewrittenBacklinkPages,
    IReadOnlyList<string> RemovedArtifactDirectories,
    string RecoveryOutcome);

public sealed partial class ResourceMutationService
{
    private const string TaskDeletionJournalKind = "task_deletion";
    private readonly IBacklinkIndex? _backlinkIndex;

    public event EventHandler<BacklinksChangedEventArgs>? BacklinksChanged;

    public ResourceMutationOutcome DeleteTask(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        string? confirmTitle,
        bool cascadeChildren,
        string? ifPreflightRevision = null)
    {
        if (string.IsNullOrWhiteSpace(mutationId)
            || string.IsNullOrWhiteSpace(ifRevision)
            || confirmTitle is null)
        {
            return new ResourceMutationOutcome(
                mutationId ?? string.Empty,
                "precondition_required",
                false,
                ifRevision,
                null,
                null,
                "mutation_id, if_revision, and confirm_title are required.");
        }

        var normalizedTaskId = taskId?.Trim();
        var payload = JsonSerializer.SerializeToElement(new
        {
            confirm_title = confirmTitle,
            cascade_children = cascadeChildren,
            if_preflight_revision = ifPreflightRevision,
        });
        var requestHash = HashRequest(
            mutationId,
            "delete_task",
            normalizedTaskId,
            ifRevision,
            payload);
        var recoveredWrites = new HashSet<string>(StringComparer.Ordinal);
        var deletedTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var backlinkChanges = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var lease = VaultScopedCoordinator.EnterExclusive(_vaultPath);
            recoveredWrites.UnionWith(RecoverUnsafe());
            var state = ReadState();
            Prune(state);
            if (state.Outcomes.TryGetValue(mutationId, out var recorded))
            {
                if (!string.Equals(recorded.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new ResourceMutationOutcome(
                        mutationId,
                        "mutation_id_reused",
                        false,
                        ifRevision,
                        null,
                        null,
                        "mutation_id was already used for a different request.");
                }

                return recorded.Outcome with { Replayed = true };
            }

            if (string.IsNullOrWhiteSpace(normalizedTaskId) || !IsSafeTaskId(normalizedTaskId))
            {
                return Record(
                    state,
                    mutationId,
                    requestHash,
                    new ResourceMutationOutcome(
                        mutationId,
                        "validation_error",
                        false,
                        ifRevision,
                        null,
                        null,
                        "task_id must be a safe Task ID."));
            }

            var plan = BuildDeletionPlanUnsafe(normalizedTaskId);
            if (plan is null)
            {
                return Record(
                    state,
                    mutationId,
                    requestHash,
                    new ResourceMutationOutcome(
                        mutationId,
                        "not_found",
                        false,
                        ifRevision,
                        null,
                        null,
                        "Task was not found."));
            }

            var currentRevision = Revision(plan.Root.Bytes);
            if (!string.Equals(ifRevision, currentRevision, StringComparison.Ordinal))
            {
                return Record(
                    state,
                    mutationId,
                    requestHash,
                    new ResourceMutationOutcome(
                        mutationId,
                        "conflict",
                        false,
                        ifRevision,
                        currentRevision,
                        Snapshot(plan.Root.Task, currentRevision),
                        "if_revision does not match the current Resource Revision."));
            }

            if (!string.Equals(confirmTitle, plan.Root.Task.Title, StringComparison.Ordinal))
            {
                return Record(
                    state,
                    mutationId,
                    requestHash,
                    new ResourceMutationOutcome(
                        mutationId,
                        "validation_error",
                        false,
                        ifRevision,
                        currentRevision,
                        Snapshot(plan.Root.Task, currentRevision),
                        "confirm_title must exactly match the current Task title."));
            }

            if (plan.Descendants.Count > 0 && !cascadeChildren)
            {
                return Record(
                    state,
                    mutationId,
                    requestHash,
                    new ResourceMutationOutcome(
                        mutationId,
                        "descendants_require_cascade",
                        false,
                        ifRevision,
                        currentRevision,
                        Snapshot(plan.Root.Task, currentRevision),
                        "Task has descendants. Set cascade_children to true to delete the complete subtree.",
                        DeletionPreflight: plan.Preflight));
            }

            if (cascadeChildren && string.IsNullOrWhiteSpace(ifPreflightRevision))
            {
                return Record(
                    state,
                    mutationId,
                    requestHash,
                    new ResourceMutationOutcome(
                        mutationId,
                        "precondition_required",
                        false,
                        ifRevision,
                        currentRevision,
                        Snapshot(plan.Root.Task, currentRevision),
                        "if_preflight_revision is required when cascade_children is true.",
                        DeletionPreflight: plan.Preflight));
            }

            if (cascadeChildren
                && !string.Equals(
                    ifPreflightRevision,
                    plan.Preflight.PreflightRevision,
                    StringComparison.Ordinal))
            {
                return RecordDeletionConflict(
                    state,
                    mutationId,
                    requestHash,
                    ifRevision,
                    plan,
                    "Task deletion impact changed after preflight.");
            }

            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeFinalValidation);
            var validatedPlan = BuildDeletionPlanUnsafe(normalizedTaskId);
            if (validatedPlan is null
                || !string.Equals(
                    DeletionPlanFingerprint(plan),
                    DeletionPlanFingerprint(validatedPlan),
                    StringComparison.Ordinal))
            {
                return RecordDeletionConflict(
                    state,
                    mutationId,
                    requestHash,
                    ifRevision,
                    validatedPlan,
                    "Task deletion impact changed before commit.");
            }
            plan = validatedPlan;

            var journal = StageDeletionOperation(
                mutationId,
                requestHash,
                ifRevision,
                plan);
            try
            {
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeJournal);
                var journalPlan = BuildDeletionPlanUnsafe(normalizedTaskId);
                if (journalPlan is null
                    || !string.Equals(
                        DeletionPlanFingerprint(plan),
                        DeletionPlanFingerprint(journalPlan),
                        StringComparison.Ordinal))
                {
                    DeleteOperationDirectory(journal);
                    return RecordDeletionConflict(
                        state,
                        mutationId,
                        requestHash,
                        ifRevision,
                        journalPlan,
                        "Task deletion impact changed while staging backups.");
                }
                WriteDeletionJournal(journal);
            }
            catch
            {
                if (!File.Exists(JournalPath))
                    DeleteOperationDirectory(journal);
                if (File.Exists(JournalPath + ".tmp"))
                    File.Delete(JournalPath + ".tmp");
                throw;
            }
            try
            {
                ApplyCommittedDeletion(journal, injectFaults: true);
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterReplacementBeforeCommit);
                WriteDeletionJournal(journal with { Committed = true });
                foreach (var rewrittenTaskId in journal.Files
                             .Where(file => file.Action == "rewrite" && file.TaskId is not null)
                             .Select(file => file.TaskId!))
                {
                    recoveredWrites.Add(rewrittenTaskId);
                }
                backlinkChanges.UnionWith(RefreshBacklinkIndex(journal));
                foreach (var deletedTask in journal.Report.DeletedTasks)
                {
                    _vault.ForgetManagedBytes(deletedTask.Id);
                    deletedTaskIds.Add(deletedTask.Id);
                }

                var outcome = Record(
                    state,
                    mutationId,
                    requestHash,
                    new ResourceMutationOutcome(
                        mutationId,
                        "applied",
                        false,
                        ifRevision,
                        null,
                        null,
                        DeletionPreflight: plan.Preflight,
                        DeletionReport: journal.Report));
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterCommit);
                DeleteJournal();
                DeleteOperationDirectory(journal);
                return outcome;
            }
            catch
            {
                recoveredWrites.UnionWith(RecoverUnsafe());
                throw;
            }
        }
        finally
        {
            NotifyRecoveredDeletes(deletedTaskIds);
            foreach (var recoveredTaskId in recoveredWrites)
                _vault.NotifyTaskWritten(recoveredTaskId);
            foreach (var deletedTaskId in deletedTaskIds)
                _vault.NotifyTaskDeleted(deletedTaskId);
            RaiseBacklinksChanged(backlinkChanges);
        }
    }

    public TaskDeletionPreflightOutcome PreflightTaskDeletion(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || !IsSafeTaskId(taskId.Trim()))
            return new TaskDeletionPreflightOutcome(
                "validation_error",
                Error: "task_id must be a safe Task ID.");

        var recoveredWrites = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var lease = VaultScopedCoordinator.EnterExclusive(_vaultPath);
            recoveredWrites.UnionWith(RecoverUnsafe());
            var plan = BuildDeletionPlanUnsafe(taskId.Trim());
            return plan is null
                ? new TaskDeletionPreflightOutcome("not_found", Error: "Task was not found.")
                : new TaskDeletionPreflightOutcome("ready", plan.Preflight);
        }
        finally
        {
            NotifyRecoveredDeletes();
            foreach (var recoveredTaskId in recoveredWrites)
                _vault.NotifyTaskWritten(recoveredTaskId);
        }
    }

    private DeletionPlan? BuildDeletionPlanUnsafe(string taskId)
    {
        var taskFiles = ReadTaskFilesUnsafe();
        if (!taskFiles.TryGetValue(taskId, out var root))
            return null;

        var descendants = FindDescendants(root.Task.Id, taskFiles);
        var deletedTasks = descendants
            .Prepend(root)
            .ToArray();
        var artifactDirectories = FindArtifactDirectories(
            deletedTasks.Select(source => source.Task.Id));
        var artifacts = artifactDirectories
            .SelectMany(directory => directory.Artifacts)
            .OrderBy(artifact => artifact.VaultRelativePath, StringComparer.Ordinal)
            .ToArray();
        var rewrites = FindBacklinkRewrites(deletedTasks, taskFiles);
        var preflight = new TaskDeletionPreflight(
            ToDeletionTask(root),
            descendants.Select(ToDeletionTask).ToArray(),
            artifacts,
            rewrites.Select(rewrite => rewrite.Page).ToArray(),
            artifactDirectories
                .Select(directory => directory.VaultRelativePath)
                .ToArray(),
            string.Empty);
        var plan = new DeletionPlan(
            root,
            descendants,
            deletedTasks,
            artifactDirectories,
            rewrites,
            preflight);
        return plan with
        {
            Preflight = preflight with
            {
                PreflightRevision = $"dpr1-{DeletionPlanFingerprint(plan)}",
            },
        };
    }

    private ResourceMutationOutcome RecordDeletionConflict(
        State state,
        string mutationId,
        string requestHash,
        string expectedRevision,
        DeletionPlan? currentPlan,
        string message)
    {
        var currentRevision = currentPlan is null
            ? null
            : Revision(currentPlan.Root.Bytes);
        return Record(
            state,
            mutationId,
            requestHash,
            new ResourceMutationOutcome(
                mutationId,
                "conflict",
                false,
                expectedRevision,
                currentRevision,
                currentPlan is null
                    ? null
                    : Snapshot(currentPlan.Root.Task, currentRevision!),
                message,
                DeletionPreflight: currentPlan?.Preflight));
    }

    private static string DeletionPlanFingerprint(DeletionPlan plan)
    {
        var fingerprint = new StringBuilder();
        foreach (var task in plan.DeletedTasks)
        {
            fingerprint.Append("task:")
                .Append(task.Task.Id)
                .Append(':')
                .Append(Revision(task.Bytes))
                .Append('\n');
        }
        foreach (var directory in plan.ArtifactDirectories)
        {
            fingerprint.Append("directory:")
                .Append(directory.VaultRelativePath)
                .Append(':')
                .Append(directory.Fingerprint)
                .Append('\n');
        }
        foreach (var rewrite in plan.BacklinkRewrites)
        {
            fingerprint.Append("rewrite:")
                .Append(rewrite.Page.VaultRelativePath)
                .Append(':')
                .Append(Revision(rewrite.OriginalBytes))
                .Append(':')
                .Append(Revision(rewrite.UpdatedBytes))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(fingerprint.ToString())))
            .ToLowerInvariant();
    }

    private Dictionary<string, DeletionTaskSource> ReadTaskFilesUnsafe()
    {
        var tasks = new Dictionary<string, DeletionTaskSource>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(path => !Path.GetFileName(path).StartsWith('_')))
        {
            var fileTaskId = Path.GetFileNameWithoutExtension(path);
            if (!IsSafeTaskId(fileTaskId))
                throw new InvalidDataException($"Task file '{Path.GetFileName(path)}' has an unsafe filename.");
            var bytes = File.ReadAllBytes(path);
            var task = _parser.Parse(Encoding.UTF8.GetString(bytes));
            if (!string.Equals(task.Id, fileTaskId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Task file '{Path.GetFileName(path)}' contains mismatched id '{task.Id}'.");
            }
            if (!tasks.TryAdd(task.Id, new DeletionTaskSource(task, bytes, path)))
                throw new InvalidDataException($"Duplicate Task id '{task.Id}' was found.");
        }

        return tasks;
    }

    private static IReadOnlyList<DeletionTaskSource> FindDescendants(
        string rootId,
        IReadOnlyDictionary<string, DeletionTaskSource> tasks)
    {
        var pbiIdByAdoId = BuildPbiAdoIdLookup(tasks);
        var descendants = new List<DeletionTaskSource>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var parents = new Queue<string>();
        parents.Enqueue(rootId);

        while (parents.Count > 0)
        {
            var parentId = parents.Dequeue();
            foreach (var child in tasks.Values
                         .Where(candidate => string.Equals(
                             ResolveParentTaskId(candidate.Task.Parent, tasks, pbiIdByAdoId),
                             parentId,
                             StringComparison.Ordinal))
                         .OrderBy(candidate => candidate.Task.Id, StringComparer.Ordinal))
            {
                if (!visited.Add(child.Task.Id))
                    continue;

                descendants.Add(child);
                parents.Enqueue(child.Task.Id);
            }
        }

        return descendants;
    }

    private static IReadOnlyDictionary<int, string> BuildPbiAdoIdLookup(
        IReadOnlyDictionary<string, DeletionTaskSource> tasks)
    {
        var resolved = new Dictionary<int, string>();
        var ambiguous = new HashSet<int>();
        foreach (var source in tasks.Values)
        {
            if (source.Task.Type != GlassworkTask.Types.Pbi || !source.Task.AdoLink.HasValue)
                continue;

            var adoId = source.Task.AdoLink.Value;
            if (!resolved.TryAdd(adoId, source.Task.Id))
            {
                resolved.Remove(adoId);
                ambiguous.Add(adoId);
            }
        }

        foreach (var adoId in ambiguous)
            resolved.Remove(adoId);
        return resolved;
    }

    private static string? ResolveParentTaskId(
        string? parent,
        IReadOnlyDictionary<string, DeletionTaskSource> tasks,
        IReadOnlyDictionary<int, string> pbiIdByAdoId)
    {
        var normalized = parent?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;
        if (tasks.ContainsKey(normalized))
            return normalized;

        var adoId = AdoParentIdExtractor.TryExtractId(normalized);
        return adoId.HasValue && pbiIdByAdoId.TryGetValue(adoId.Value, out var taskId)
            ? taskId
            : null;
    }

    private static TaskDeletionTask ToDeletionTask(DeletionTaskSource source) =>
        new(source.Task.Id, source.Task.Title, Revision(source.Bytes));

    private IReadOnlyList<ArtifactDirectoryPlan> FindArtifactDirectories(
        IEnumerable<string> taskIds)
    {
        var vaultRoot = VaultPathResolver.Resolve(_vaultPath).VaultRoot;
        var directories = new List<ArtifactDirectoryPlan>();
        foreach (var taskId in taskIds)
        {
            var directory = Path.Combine(_vaultPath, $"{taskId}.artifacts");
            if (!Directory.Exists(directory))
                continue;

            var tree = ReadDirectoryTree(directory);
            var artifacts = tree.Files
                .Select(path => new TaskDeletionArtifact(
                    taskId,
                    NormalizeRelativePath(Path.GetRelativePath(vaultRoot, path))))
                .OrderBy(artifact => artifact.VaultRelativePath, StringComparer.Ordinal)
                .ToArray();
            directories.Add(new ArtifactDirectoryPlan(
                taskId,
                directory,
                NormalizeRelativePath(Path.GetRelativePath(vaultRoot, directory)),
                artifacts,
                DirectoryFingerprint(tree, directory)));
        }

        return directories
            .OrderBy(directory => directory.VaultRelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<BacklinkRewrite> FindBacklinkRewrites(
        IReadOnlyList<DeletionTaskSource> deletedTasks,
        IReadOnlyDictionary<string, DeletionTaskSource> allTasks)
    {
        var vaultRoot = VaultPathResolver.Resolve(_vaultPath).VaultRoot;
        var deletedPaths = deletedTasks
            .Select(source => Path.GetFullPath(source.Path))
            .ToHashSet(PathComparer);
        var deletedArtifactPrefixes = deletedTasks
            .Select(source => NormalizeDirectoryPrefix(
                Path.Combine(_vaultPath, $"{source.Task.Id}.artifacts")))
            .ToArray();
        var titles = deletedTasks.ToDictionary(
            source => source.Task.Id,
            source => source.Task.Title,
            StringComparer.Ordinal);
        var rewrites = new List<BacklinkRewrite>();

        foreach (var path in Directory.EnumerateFiles(vaultRoot, "*.md", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(path);
            if (deletedPaths.Contains(fullPath)
                || deletedArtifactPrefixes.Any(prefix => IsUnderPrefix(fullPath, prefix))
                || IsGeneratedVaultFile(fullPath))
            {
                continue;
            }

            var originalBytes = File.ReadAllBytes(fullPath);
            if (!MightContainDeletedWikiLink(originalBytes, titles.Keys))
                continue;

            TextFileContent content;
            try
            {
                content = DecodeTextFile(originalBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException(
                    $"Vault page '{NormalizeRelativePath(Path.GetRelativePath(vaultRoot, fullPath))}' "
                    + "contains a candidate Task Wiki link but its encoding cannot be decoded safely.",
                    ex);
            }
            var updated = RewriteWikiLinks(content.Text, titles, out var replacementCount);
            if (replacementCount == 0)
                continue;

            var updatedBytes = content.Encode(updated);
            rewrites.Add(new BacklinkRewrite(
                fullPath,
                originalBytes,
                updatedBytes,
                allTasks.Values.FirstOrDefault(source => PathComparer.Equals(
                    Path.GetFullPath(source.Path),
                    fullPath))?.Task.Id,
                new TaskDeletionBacklinkPage(
                    NormalizeRelativePath(Path.GetRelativePath(vaultRoot, fullPath)),
                    replacementCount)));
        }

        return rewrites
            .OrderBy(rewrite => rewrite.Page.VaultRelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MightContainDeletedWikiLink(
        byte[] bytes,
        IEnumerable<string> taskIds)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })
            || bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })
            || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })
            || bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return true;
        }

        foreach (var taskId in taskIds)
        {
            var encodedId = Encoding.ASCII.GetBytes(taskId);
            for (var linkStart = 0; linkStart + 3 < bytes.Length; linkStart++)
            {
                if (bytes[linkStart] != (byte)'['
                    || bytes[linkStart + 1] != (byte)'[')
                    continue;
                var linkEnd = IndexOfSequence(
                    bytes,
                    new byte[] { (byte)']', (byte)']' },
                    linkStart + 2);
                if (linkEnd < 0)
                    break;
                if (IndexOfSequence(bytes, encodedId, linkStart + 2, linkEnd) >= 0)
                    return true;
                linkStart = linkEnd + 1;
            }
        }

        return false;
    }

    private static int IndexOfSequence(
        byte[] bytes,
        byte[] sequence,
        int start,
        int? endExclusive = null)
    {
        var end = Math.Min(endExclusive ?? bytes.Length, bytes.Length);
        for (var index = start; index + sequence.Length <= end; index++)
        {
            if (bytes.AsSpan(index, sequence.Length).SequenceEqual(sequence))
                return index;
        }
        return -1;
    }

    private bool IsGeneratedVaultFile(string fullPath)
    {
        var relative = Path.GetRelativePath(
            VaultPathResolver.Resolve(_vaultPath).VaultRoot,
            fullPath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Equals(".glasswork", StringComparison.OrdinalIgnoreCase)))
            return true;

        return PathComparer.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(_vaultPath))
            && Path.GetFileName(fullPath).StartsWith('_');
    }

    private static string RewriteWikiLinks(
        string content,
        IReadOnlyDictionary<string, string> titles,
        out int replacementCount)
    {
        var matches = WikiLinkParser.Find(content)
            .Where(match => titles.ContainsKey(match.Stem))
            .ToArray();
        replacementCount = matches.Length;
        if (matches.Length == 0)
            return content;

        var updated = new StringBuilder(content);
        for (var index = matches.Length - 1; index >= 0; index--)
        {
            var match = matches[index];
            var replacement = match.Display ?? titles[match.Stem];
            updated.Remove(match.Index, match.Length);
            updated.Insert(match.Index, replacement);
        }

        return updated.ToString();
    }

    private static TextFileContent DecodeTextFile(byte[] bytes)
    {
        var (encoding, preambleLength) = DetectEncoding(bytes);
        return new TextFileContent(
            encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength),
            encoding,
            bytes[..preambleLength]);
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true), 4);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true), 2);
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0);
    }

    private static string NormalizeDirectoryPrefix(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private static bool IsUnderPrefix(string path, string prefix) =>
        Path.GetFullPath(path).StartsWith(prefix, PathComparison);

    private static string DirectoryFingerprint(string directory) =>
        DirectoryFingerprint(ReadDirectoryTree(directory), directory);

    private static string DirectoryFingerprint(DirectoryTree tree, string directory)
    {
        var fingerprint = new StringBuilder();
        foreach (var childDirectory in tree.Directories.OrderBy(path => path, PathComparer))
        {
            fingerprint.Append("directory:")
                .Append(NormalizeRelativePath(Path.GetRelativePath(directory, childDirectory)))
                .Append('\n');
        }
        foreach (var file in tree.Files.OrderBy(path => path, PathComparer))
        {
            fingerprint.Append("file:")
                .Append(NormalizeRelativePath(Path.GetRelativePath(directory, file)))
                .Append(':')
                .Append(Revision(File.ReadAllBytes(file)))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(fingerprint.ToString())))
            .ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private DeletionJournal StageDeletionOperation(
        string mutationId,
        string requestHash,
        string expectedRevision,
        DeletionPlan plan)
    {
        EnsureDeletionStagingTreeIsPhysical();
        var operationId = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{mutationId}\n{requestHash}")))
            .ToLowerInvariant()[..24];
        var operationDirectory = Path.Combine(DeletionOperationsPath, operationId);
        if (Directory.Exists(operationDirectory))
            DeleteDirectoryTreeSafely(operationDirectory);
        Directory.CreateDirectory(operationDirectory);

        try
        {
            var vaultRoot = VaultPathResolver.Resolve(_vaultPath).VaultRoot;
            var fileEntries = new List<DeletionJournalFile>();
            var fileIndex = 0;
            foreach (var rewrite in plan.BacklinkRewrites)
            {
                var backupRelativePath = $"files/{fileIndex:D4}.original";
                var stagedRelativePath = $"files/{fileIndex:D4}.updated";
                WriteDurableFile(
                    Path.Combine(operationDirectory, backupRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    rewrite.OriginalBytes);
                WriteDurableFile(
                    Path.Combine(operationDirectory, stagedRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    rewrite.UpdatedBytes);
                fileEntries.Add(new DeletionJournalFile(
                    NormalizeRelativePath(Path.GetRelativePath(vaultRoot, rewrite.Path)),
                    backupRelativePath,
                    stagedRelativePath,
                    "rewrite",
                    rewrite.TaskId,
                    Revision(rewrite.OriginalBytes),
                    Revision(rewrite.UpdatedBytes)));
                fileIndex++;
            }

            foreach (var source in plan.Descendants.Reverse().Append(plan.Root))
            {
                var backupRelativePath = $"files/{fileIndex:D4}.original";
                WriteDurableFile(
                    Path.Combine(operationDirectory, backupRelativePath.Replace('/', Path.DirectorySeparatorChar)),
                    source.Bytes);
                fileEntries.Add(new DeletionJournalFile(
                    NormalizeRelativePath(Path.GetRelativePath(vaultRoot, source.Path)),
                    backupRelativePath,
                    null,
                    "delete",
                    source.Task.Id,
                    Revision(source.Bytes),
                    null));
                fileIndex++;
            }

            var directoryEntries = new List<DeletionJournalDirectory>();
            for (var index = 0; index < plan.ArtifactDirectories.Count; index++)
            {
                var source = plan.ArtifactDirectories[index];
                var backupRelativePath = $"directories/{index:D4}";
                var removedRelativePath = $"removed/{index:D4}";
                CopyDirectory(
                    source.Path,
                    Path.Combine(operationDirectory, backupRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                directoryEntries.Add(new DeletionJournalDirectory(
                    source.VaultRelativePath,
                    backupRelativePath,
                    removedRelativePath,
                    source.Fingerprint));
            }

            var report = new TaskDeletionReport(
                plan.DeletedTasks.Select(ToDeletionTask).ToArray(),
                plan.Descendants.Select(ToDeletionTask).ToArray(),
                plan.Preflight.Artifacts,
                plan.Preflight.BacklinkPages,
                plan.Preflight.ArtifactDirectories,
                "not_required");
            return new DeletionJournal(
                TaskDeletionJournalKind,
                mutationId,
                requestHash,
                expectedRevision,
                Committed: false,
                operationId,
                plan.Preflight,
                report,
                fileEntries,
                directoryEntries);
        }
        catch
        {
            if (Directory.Exists(operationDirectory))
                DeleteDirectoryTreeSafely(operationDirectory);
            throw;
        }
    }

    private void ApplyCommittedDeletion(DeletionJournal journal, bool injectFaults)
    {
        ValidateDeletionBackups(journal);
        foreach (var file in journal.Files)
            _ = NeedsForwardFileApply(journal, file);
        foreach (var directory in journal.Directories)
            ValidateForwardDirectoryState(journal, directory);

        foreach (var file in journal.Files.Where(file => file.Action == "rewrite"))
        {
            if (injectFaults)
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
            if (!NeedsForwardFileApply(journal, file))
                continue;
            var target = ResolveVaultRelativePath(file.VaultRelativePath);
            var updated = ReadVerifiedOperationFile(
                journal,
                file.StagedRelativePath!,
                file.StagedRevision!);
            WriteVaultFile(target, file.TaskId, updated);
        }

        foreach (var directory in journal.Directories)
        {
            if (injectFaults)
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
            MoveDirectoryForDeletion(journal, directory);
        }

        foreach (var file in journal.Files.Where(file => file.Action == "delete"))
        {
            if (injectFaults)
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
            if (!NeedsForwardFileApply(journal, file))
                continue;
            if (file.TaskId is null)
                throw new InvalidDataException("Task deletion journal is missing a Task ID.");
            _vault.DeleteUnsafe(file.TaskId);
        }
    }

    private IReadOnlyList<string> RecoverTaskDeletionUnsafe(JsonElement root)
    {
        var journal = JsonSerializer.Deserialize<DeletionJournal>(root.GetRawText())
            ?? throw new InvalidDataException("Task deletion journal is invalid.");
        ValidateDeletionJournal(journal);

        var recoveredWrites = new HashSet<string>(StringComparer.Ordinal);
        if (journal.Committed)
        {
            ApplyCommittedDeletion(journal, injectFaults: false);
            lock (_recoveredDeletesGate)
            {
                foreach (var task in journal.Report.DeletedTasks)
                    _recoveredDeletes.Add(task.Id);
            }

            foreach (var taskId in journal.Files
                         .Where(file => file.Action == "rewrite" && file.TaskId is not null)
                         .Select(file => file.TaskId!))
            {
                recoveredWrites.Add(taskId);
            }
            RaiseBacklinksChanged(RefreshBacklinkIndex(journal));

            var state = ReadState();
            Prune(state);
            if (state.Outcomes.TryGetValue(journal.MutationId, out var recorded)
                && !string.Equals(
                    recorded.RequestHash,
                    journal.RequestHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Task deletion recovery found a mismatched mutation outcome.");
            }

            var recoveredReport = journal.Report with
            {
                RecoveryOutcome = "completed_after_recovery",
            };
            state.Outcomes[journal.MutationId] = new RecordedOutcome(
                journal.RequestHash,
                _clock(),
                new ResourceMutationOutcome(
                    journal.MutationId,
                    "applied",
                    false,
                    journal.ExpectedRevision,
                    null,
                    null,
                    DeletionPreflight: journal.Preflight,
                    DeletionReport: recoveredReport));
            WriteState(state);
        }
        else
        {
            RollBackDeletion(journal);
            RaiseBacklinksChanged(RefreshBacklinkIndex(journal));
            foreach (var taskId in journal.Files
                         .Where(file => file.TaskId is not null)
                         .Select(file => file.TaskId!))
            {
                recoveredWrites.Add(taskId);
            }
        }

        DeleteJournal();
        DeleteOperationDirectory(journal);
        return recoveredWrites.ToArray();
    }

    private IReadOnlyCollection<string> RefreshBacklinkIndex(DeletionJournal journal)
    {
        if (_backlinkIndex is null)
            return Array.Empty<string>();

        var affected = new HashSet<string>(StringComparer.Ordinal);
        var vaultRoot = VaultPathResolver.Resolve(_vaultPath).VaultRoot;
        foreach (var file in journal.Files.Where(file => file.Action == "rewrite"))
        {
            affected.UnionWith(_backlinkIndex.UpdateForFile(
                vaultRoot,
                ResolveVaultRelativePath(file.VaultRelativePath)));
        }

        return affected;
    }

    private void RaiseBacklinksChanged(IReadOnlyCollection<string> affectedTaskIds)
    {
        if (affectedTaskIds.Count == 0)
            return;
        try
        {
            BacklinksChanged?.Invoke(
                this,
                new BacklinksChangedEventArgs(affectedTaskIds));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"BacklinksChanged subscriber threw after Task deletion: {ex}");
        }
    }

    private void RollBackDeletion(DeletionJournal journal)
    {
        ValidateDeletionBackups(journal);
        foreach (var file in journal.Files)
            _ = NeedsFileRollback(journal, file);
        foreach (var directory in journal.Directories)
            ValidateDirectoryRollbackState(journal, directory);

        foreach (var file in journal.Files)
        {
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringRecovery);
            if (!NeedsFileRollback(journal, file))
                continue;
            var target = ResolveVaultRelativePath(file.VaultRelativePath);
            var original = ReadVerifiedOperationFile(
                journal,
                file.BackupRelativePath,
                file.OriginalRevision);
            WriteVaultFile(target, file.TaskId, original);
        }

        foreach (var directory in journal.Directories)
        {
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringRecovery);
            var target = ResolveVaultRelativePath(directory.VaultRelativePath);
            var removed = ResolveOperationPath(journal, directory.RemovedRelativePath);
            if (Directory.Exists(target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(removed, target);
        }
    }

    private bool NeedsFileRollback(
        DeletionJournal journal,
        DeletionJournalFile file)
    {
        var target = ResolveVaultRelativePath(file.VaultRelativePath);
        var original = ReadVerifiedOperationFile(
            journal,
            file.BackupRelativePath,
            file.OriginalRevision);
        var current = File.Exists(target) ? File.ReadAllBytes(target) : null;

        if (file.Action == "delete")
        {
            if (current is null)
                return true;
            if (current.AsSpan().SequenceEqual(original))
                return false;
        }
        else
        {
            if (current is null)
                throw new ResourceRevisionConflictException(
                    $"Vault page '{file.VaultRelativePath}' was removed after the deletion journal was written.");
            if (current.AsSpan().SequenceEqual(original))
                return false;
            var staged = ReadVerifiedOperationFile(
                journal,
                file.StagedRelativePath!,
                file.StagedRevision!);
            if (current.AsSpan().SequenceEqual(staged))
                return true;
        }

        throw new ResourceRevisionConflictException(
            $"Vault path '{file.VaultRelativePath}' changed after the deletion journal was written.");
    }

    private bool NeedsForwardFileApply(
        DeletionJournal journal,
        DeletionJournalFile file)
    {
        var target = ResolveVaultRelativePath(file.VaultRelativePath);
        var current = File.Exists(target) ? File.ReadAllBytes(target) : null;
        var original = ReadVerifiedOperationFile(
            journal,
            file.BackupRelativePath,
            file.OriginalRevision);
        if (file.Action == "delete")
        {
            if (current is null)
                return false;
            if (current.AsSpan().SequenceEqual(original))
                return true;
        }
        else
        {
            if (current is null)
            {
                throw new ResourceRevisionConflictException(
                    $"Vault page '{file.VaultRelativePath}' disappeared before deletion.");
            }
            if (current.AsSpan().SequenceEqual(original))
                return true;
            var staged = ReadVerifiedOperationFile(
                journal,
                file.StagedRelativePath!,
                file.StagedRevision!);
            if (current.AsSpan().SequenceEqual(staged))
                return false;
        }

        throw new ResourceRevisionConflictException(
            $"Vault path '{file.VaultRelativePath}' changed before deletion.");
    }

    private void MoveDirectoryForDeletion(
        DeletionJournal journal,
        DeletionJournalDirectory directory)
    {
        var target = ResolveVaultRelativePath(directory.VaultRelativePath);
        var removed = ResolveOperationPath(journal, directory.RemovedRelativePath);
        var targetExists = Directory.Exists(target);
        var removedExists = Directory.Exists(removed);
        if (targetExists && removedExists)
        {
            throw new ResourceRevisionConflictException(
                $"Artifact directory '{directory.VaultRelativePath}' exists in both live and staged-deletion locations.");
        }
        if (!targetExists && removedExists)
            return;
        if (!targetExists)
            throw new ResourceRevisionConflictException(
                $"Artifact directory '{directory.VaultRelativePath}' disappeared before deletion.");
        if (!string.Equals(
            DirectoryFingerprint(target),
            directory.OriginalFingerprint,
            StringComparison.Ordinal))
        {
            throw new ResourceRevisionConflictException(
                $"Artifact directory '{directory.VaultRelativePath}' changed before deletion.");
        }

        RegisterDirectoryWrites(target);
        Directory.CreateDirectory(Path.GetDirectoryName(removed)!);
        Directory.Move(target, removed);
    }

    private void ValidateForwardDirectoryState(
        DeletionJournal journal,
        DeletionJournalDirectory directory)
    {
        var target = ResolveVaultRelativePath(directory.VaultRelativePath);
        var removed = ResolveOperationPath(journal, directory.RemovedRelativePath);
        var targetExists = Directory.Exists(target);
        var removedExists = Directory.Exists(removed);
        if (targetExists == removedExists)
        {
            throw new ResourceRevisionConflictException(
                $"Artifact directory '{directory.VaultRelativePath}' is not in a valid deletion state.");
        }

        var existing = targetExists ? target : removed;
        if (!string.Equals(
            DirectoryFingerprint(existing),
            directory.OriginalFingerprint,
            StringComparison.Ordinal))
        {
            throw new ResourceRevisionConflictException(
                $"Artifact directory '{directory.VaultRelativePath}' changed before deletion.");
        }
    }

    private void ValidateDirectoryRollbackState(
        DeletionJournal journal,
        DeletionJournalDirectory directory)
    {
        var target = ResolveVaultRelativePath(directory.VaultRelativePath);
        var removed = ResolveOperationPath(journal, directory.RemovedRelativePath);
        var targetExists = Directory.Exists(target);
        var removedExists = Directory.Exists(removed);
        if (targetExists == removedExists)
        {
            throw new ResourceRevisionConflictException(
                $"Artifact directory '{directory.VaultRelativePath}' is not in a recoverable deletion state.");
        }

        var existing = targetExists ? target : removed;
        if (!string.Equals(
            DirectoryFingerprint(existing),
            directory.OriginalFingerprint,
            StringComparison.Ordinal))
        {
            throw new ResourceRevisionConflictException(
                $"Artifact directory '{directory.VaultRelativePath}' changed after the deletion journal was written.");
        }
    }

    private void WriteVaultFile(string path, string? taskId, byte[] bytes)
    {
        if (taskId is not null
            && PathComparer.Equals(
                Path.GetDirectoryName(Path.GetFullPath(path)),
                Path.GetFullPath(_vaultPath)))
        {
            _vault.ReplaceBytesUnsafe(taskId, bytes);
            _vault.RememberManagedBytes(taskId, bytes);
            return;
        }

        _vault.RegisterSelfWrite(path);
        var temp = path + $".mutation.tmp.{Guid.NewGuid():N}";
        WriteDurableFile(temp, bytes);
        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }

    private void RegisterDirectoryWrites(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in ReadDirectoryTree(directory).Files)
            _vault.RegisterSelfWrite(path);
    }

    private void WriteDeletionJournal(DeletionJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        WriteDurableFile(
            JournalPath + ".tmp",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal)));
        if (File.Exists(JournalPath))
            File.Replace(JournalPath + ".tmp", JournalPath, null);
        else
            File.Move(JournalPath + ".tmp", JournalPath);
    }

    private static void WriteDurableFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        var tree = ReadDirectoryTree(source);
        Directory.CreateDirectory(destination);
        foreach (var directory in tree.Directories)
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in tree.Files)
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private string ResolveVaultRelativePath(string relativePath)
    {
        var vaultRoot = VaultPathResolver.Resolve(_vaultPath).VaultRoot;
        var resolved = Path.GetFullPath(Path.Combine(
            vaultRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = NormalizeDirectoryPrefix(vaultRoot);
        if (!resolved.StartsWith(prefix, PathComparison))
            throw new InvalidDataException("Task deletion journal path escapes the Vault.");
        return resolved;
    }

    private byte[] ReadOperationFile(DeletionJournal journal, string relativePath) =>
        File.ReadAllBytes(ResolveOperationPath(journal, relativePath));

    private byte[] ReadVerifiedOperationFile(
        DeletionJournal journal,
        string relativePath,
        string expectedRevision)
    {
        var bytes = ReadOperationFile(journal, relativePath);
        if (!string.Equals(Revision(bytes), expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Task deletion staged file '{relativePath}' failed its integrity check.");
        }
        return bytes;
    }

    private void ValidateDeletionBackups(DeletionJournal journal)
    {
        foreach (var file in journal.Files)
        {
            var original = ReadVerifiedOperationFile(
                journal,
                file.BackupRelativePath,
                file.OriginalRevision);
            if (file.Action == "delete")
            {
                var task = _parser.Parse(Encoding.UTF8.GetString(original));
                if (!string.Equals(task.Id, file.TaskId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Task deletion backup for '{file.TaskId}' contains mismatched id '{task.Id}'.");
                }
            }
            if (file.StagedRelativePath is not null)
            {
                _ = ReadVerifiedOperationFile(
                    journal,
                    file.StagedRelativePath,
                    file.StagedRevision!);
            }
        }

        foreach (var directory in journal.Directories)
        {
            var backup = ResolveOperationPath(journal, directory.BackupRelativePath);
            if (!Directory.Exists(backup)
                || !string.Equals(
                    DirectoryFingerprint(backup),
                    directory.OriginalFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Task deletion backup directory '{directory.BackupRelativePath}' failed its integrity check.");
            }
        }
    }

    private string ResolveOperationPath(DeletionJournal journal, string relativePath)
    {
        EnsureDeletionStagingTreeIsPhysical();
        if (!IsSafeOperationId(journal.OperationId))
            throw new InvalidDataException("Task deletion operation ID is invalid.");
        var operationDirectory = Path.Combine(DeletionOperationsPath, journal.OperationId);
        var resolved = Path.GetFullPath(Path.Combine(
            operationDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = NormalizeDirectoryPrefix(operationDirectory);
        if (!resolved.StartsWith(prefix, PathComparison))
            throw new InvalidDataException("Task deletion backup path escapes the operation directory.");
        return resolved;
    }

    private string DeletionOperationsPath =>
        Path.Combine(_vaultPath, ".glasswork", "deletion-operations");

    private bool HasPendingDeletionOperations() =>
        Directory.Exists(DeletionOperationsPath)
        && Directory.EnumerateFileSystemEntries(DeletionOperationsPath).Any();

    private void CleanupOrphanDeletionOperations()
    {
        if (!Directory.Exists(DeletionOperationsPath))
            return;
        EnsureDeletionStagingTreeIsPhysical();

        foreach (var directory in Directory.EnumerateDirectories(
                     DeletionOperationsPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            DeleteDirectoryTreeSafely(directory);
        }
        foreach (var file in Directory.EnumerateFiles(
                     DeletionOperationsPath,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
        if (!Directory.EnumerateFileSystemEntries(DeletionOperationsPath).Any())
            Directory.Delete(DeletionOperationsPath);
    }

    private void DeleteOperationDirectory(DeletionJournal journal)
    {
        EnsureDeletionStagingTreeIsPhysical();
        if (!IsSafeOperationId(journal.OperationId))
            throw new InvalidDataException("Task deletion operation ID is invalid.");
        var operationDirectory = Path.Combine(DeletionOperationsPath, journal.OperationId);
        if (Directory.Exists(operationDirectory))
            DeleteDirectoryTreeSafely(operationDirectory);
    }

    private void EnsureDeletionStagingTreeIsPhysical()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_vaultPath, ".glasswork"),
                     DeletionOperationsPath,
                 })
        {
            if (!Directory.Exists(path))
                continue;
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Task deletion staging path '{path}' cannot be a reparse point.");
            }
        }
    }

    private static void DeleteDirectoryTreeSafely(string path)
    {
        if (!Directory.Exists(path))
            return;
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }

        foreach (var child in Directory.EnumerateDirectories(
                     path,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            DeleteDirectoryTreeSafely(child);
        }
        foreach (var file in Directory.EnumerateFiles(
                     path,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
        Directory.Delete(path, recursive: false);
    }

    private static DirectoryTree ReadDirectoryTree(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Directory '{root}' cannot be a reparse point.");

        var directories = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((new FileInfo(file).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"File '{file}' cannot be a reparse point.");
                files.Add(file);
            }
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Directory '{directory}' cannot be a reparse point.");
                directories.Add(directory);
                pending.Push(directory);
            }
        }

        return new DirectoryTree(directories, files);
    }

    private void ValidateDeletionJournal(DeletionJournal journal)
    {
        if (!string.Equals(journal.Kind, TaskDeletionJournalKind, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(journal.MutationId)
            || string.IsNullOrWhiteSpace(journal.RequestHash)
            || string.IsNullOrWhiteSpace(journal.ExpectedRevision)
            || !IsSafeOperationId(journal.OperationId)
            || journal.Preflight is null
            || string.IsNullOrWhiteSpace(journal.Preflight.PreflightRevision)
            || journal.Preflight.Descendants is null
            || journal.Preflight.Artifacts is null
            || journal.Preflight.BacklinkPages is null
            || journal.Preflight.ArtifactDirectories is null
            || journal.Report is null
            || journal.Report.DeletedTasks is null
            || journal.Report.Descendants is null
            || journal.Report.RemovedArtifacts is null
            || journal.Report.RewrittenBacklinkPages is null
            || journal.Report.RemovedArtifactDirectories is null
            || journal.Files is null
            || journal.Directories is null)
        {
            throw new InvalidDataException("Task deletion journal is invalid.");
        }

        foreach (var file in journal.Files)
        {
            if (file is null
                || file.Action is not ("rewrite" or "delete")
                || string.IsNullOrWhiteSpace(file.VaultRelativePath)
                || string.IsNullOrWhiteSpace(file.BackupRelativePath)
                || (file.Action == "rewrite" && string.IsNullOrWhiteSpace(file.StagedRelativePath))
                || string.IsNullOrWhiteSpace(file.OriginalRevision)
                || (file.StagedRelativePath is not null
                    && string.IsNullOrWhiteSpace(file.StagedRevision))
                || (file.Action == "delete"
                    && (string.IsNullOrWhiteSpace(file.TaskId) || !IsSafeTaskId(file.TaskId))))
            {
                throw new InvalidDataException("Task deletion journal contains an invalid file entry.");
            }

            var target = ResolveVaultRelativePath(file.VaultRelativePath);
            _ = ResolveOperationPath(journal, file.BackupRelativePath);
            if (file.StagedRelativePath is not null)
                _ = ResolveOperationPath(journal, file.StagedRelativePath);
            if (file.TaskId is not null)
            {
                if (!IsSafeTaskId(file.TaskId))
                    throw new InvalidDataException(
                        "Task deletion journal contains an unsafe Task ID.");
                var expectedTaskPath = Path.GetFullPath(
                    Path.Combine(_vaultPath, $"{file.TaskId}.md"));
                if (!PathComparer.Equals(target, expectedTaskPath))
                    throw new InvalidDataException(
                        "Task deletion journal Task ID does not match its target path.");
            }
        }

        foreach (var directory in journal.Directories)
        {
            if (directory is null
                || string.IsNullOrWhiteSpace(directory.VaultRelativePath)
                || string.IsNullOrWhiteSpace(directory.BackupRelativePath)
                || string.IsNullOrWhiteSpace(directory.RemovedRelativePath)
                || string.IsNullOrWhiteSpace(directory.OriginalFingerprint))
            {
                throw new InvalidDataException(
                    "Task deletion journal contains an invalid directory entry.");
            }

            _ = ResolveVaultRelativePath(directory.VaultRelativePath);
            _ = ResolveOperationPath(journal, directory.BackupRelativePath);
            _ = ResolveOperationPath(journal, directory.RemovedRelativePath);
        }

        ValidateDeletionManifest(journal);
    }

    private static void ValidateDeletionManifest(DeletionJournal journal)
    {
        var expectedDeletedTasks = journal.Preflight.Descendants
            .Prepend(journal.Preflight.Task)
            .ToArray();
        if (!journal.Report.DeletedTasks.SequenceEqual(expectedDeletedTasks)
            || !journal.Report.Descendants.SequenceEqual(journal.Preflight.Descendants)
            || !journal.Report.RemovedArtifacts.SequenceEqual(journal.Preflight.Artifacts)
            || !journal.Report.RewrittenBacklinkPages.SequenceEqual(journal.Preflight.BacklinkPages)
            || !journal.Report.RemovedArtifactDirectories.SequenceEqual(
                journal.Preflight.ArtifactDirectories))
        {
            throw new InvalidDataException(
                "Task deletion journal report does not match its reviewed preflight.");
        }

        var expectedTaskIds = expectedDeletedTasks
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        var deleteFiles = journal.Files
            .Where(file => file.Action == "delete")
            .ToArray();
        var deleteTaskIds = deleteFiles
            .Select(file => file.TaskId!)
            .ToArray();
        if (deleteTaskIds.Distinct(StringComparer.Ordinal).Count() != deleteTaskIds.Length
            || !expectedTaskIds.SetEquals(deleteTaskIds))
        {
            throw new InvalidDataException(
                "Task deletion journal does not contain the complete Task deletion manifest.");
        }
        var expectedTaskById = expectedDeletedTasks.ToDictionary(
            task => task.Id,
            StringComparer.Ordinal);
        foreach (var file in deleteFiles)
        {
            if (!string.Equals(
                file.OriginalRevision,
                expectedTaskById[file.TaskId!].ResourceRevision,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Task deletion journal revision for '{file.TaskId}' does not match preflight.");
            }
        }

        var expectedRewritePaths = journal.Preflight.BacklinkPages
            .Select(page => page.VaultRelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var rewritePaths = journal.Files
            .Where(file => file.Action == "rewrite")
            .Select(file => file.VaultRelativePath)
            .ToArray();
        if (rewritePaths.Distinct(StringComparer.Ordinal).Count() != rewritePaths.Length
            || !expectedRewritePaths.SetEquals(rewritePaths))
        {
            throw new InvalidDataException(
                "Task deletion journal does not contain the complete Wiki-link rewrite manifest.");
        }

        var directoryPaths = journal.Directories
            .Select(directory => directory.VaultRelativePath)
            .ToArray();
        if (directoryPaths.Distinct(StringComparer.Ordinal).Count() != directoryPaths.Length
            || !journal.Preflight.ArtifactDirectories
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(directoryPaths))
        {
            throw new InvalidDataException(
                "Task deletion journal does not contain the complete Artifact-directory manifest.");
        }

        foreach (var artifact in journal.Preflight.Artifacts)
        {
            var expectedDirectory = $"wiki/todo/{artifact.TaskId}.artifacts";
            if (!expectedTaskIds.Contains(artifact.TaskId)
                || !journal.Preflight.ArtifactDirectories.Contains(
                    expectedDirectory,
                    StringComparer.Ordinal)
                || !artifact.VaultRelativePath.StartsWith(
                    expectedDirectory + "/",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Task deletion Artifact '{artifact.VaultRelativePath}' is outside its owned directory.");
            }
        }
    }

    private static bool IsSafeOperationId(string? operationId) =>
        !string.IsNullOrWhiteSpace(operationId)
        && operationId.Length <= 64
        && operationId.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');

    private sealed record DeletionTaskSource(GlassworkTask Task, byte[] Bytes, string Path);
    private sealed record ArtifactDirectoryPlan(
        string TaskId,
        string Path,
        string VaultRelativePath,
        IReadOnlyList<TaskDeletionArtifact> Artifacts,
        string Fingerprint);
    private sealed record DirectoryTree(
        IReadOnlyList<string> Directories,
        IReadOnlyList<string> Files);
    private sealed record BacklinkRewrite(
        string Path,
        byte[] OriginalBytes,
        byte[] UpdatedBytes,
        string? TaskId,
        TaskDeletionBacklinkPage Page);
    private sealed record DeletionPlan(
        DeletionTaskSource Root,
        IReadOnlyList<DeletionTaskSource> Descendants,
        IReadOnlyList<DeletionTaskSource> DeletedTasks,
        IReadOnlyList<ArtifactDirectoryPlan> ArtifactDirectories,
        IReadOnlyList<BacklinkRewrite> BacklinkRewrites,
        TaskDeletionPreflight Preflight);
    private sealed record DeletionJournal(
        string Kind,
        string MutationId,
        string RequestHash,
        string ExpectedRevision,
        bool Committed,
        string OperationId,
        TaskDeletionPreflight Preflight,
        TaskDeletionReport Report,
        IReadOnlyList<DeletionJournalFile> Files,
        IReadOnlyList<DeletionJournalDirectory> Directories);
    private sealed record DeletionJournalFile(
        string VaultRelativePath,
        string BackupRelativePath,
        string? StagedRelativePath,
        string Action,
        string? TaskId,
        string OriginalRevision,
        string? StagedRevision);
    private sealed record DeletionJournalDirectory(
        string VaultRelativePath,
        string BackupRelativePath,
        string RemovedRelativePath,
        string OriginalFingerprint);
    private sealed record TextFileContent(string Text, Encoding Encoding, byte[] Preamble)
    {
        public byte[] Encode(string text)
        {
            var body = Encoding.GetBytes(text);
            if (Preamble.Length == 0)
                return body;

            var bytes = new byte[Preamble.Length + body.Length];
            Preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, Preamble.Length);
            return bytes;
        }
    }
}
