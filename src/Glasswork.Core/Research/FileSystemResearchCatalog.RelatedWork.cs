using Glasswork.Core.Models;
using Glasswork.Core.Services;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Glasswork.Core.Research;

public sealed partial class FileSystemResearchCatalog
{
    internal Action? BeforeRelatedWorkFileReplaceHook { get; set; }
    internal Action? BeforeCreatedTaskRollbackHook { get; set; }

    public ResearchRelatedWorkResult CreateRelatedTask(
        string topicId,
        ResearchTaskDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            if (!TryGetRelatedWorkServices(out var unavailable))
                return unavailable;
            var title = draft.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.InvalidTitle,
                    "Task title is required.");
            }
            if (string.IsNullOrWhiteSpace(VaultService.GenerateId(title)))
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.InvalidTitle,
                    "Task title must contain at least one ASCII letter or number so Glasswork can create a stable Task ID.");
            }
            if (!TryGetTopicCandidate(topicId, out var topic, out var topicFailure))
                return topicFailure;

            var topicLink = CreateTopicLink(topic);
            GlassworkTask created;
            try
            {
                created = _taskService!.CreateTask(
                    title,
                    draft.Priority,
                    adoLink: draft.AdoLink,
                    adoTitle: draft.AdoTitle,
                    description: draft.Description,
                    addToMyDay: draft.AddToMyDay,
                    relatedLinks: [topicLink]);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.WriteFailed,
                    $"Task '{title}' could not be created: {ex.Message}");
            }

            var write = WriteRelatedTaskIds(topic, created.Id, included: true);
            if (!write.Succeeded)
            {
                try
                {
                    BeforeCreatedTaskRollbackHook?.Invoke();
                    if (!_taskVault!.TryDeleteCreatedTask(
                            created.Id,
                            created.ResourceRevision!,
                            out var rollbackConflict))
                    {
                        return ResearchRelatedWorkResult.Failure(
                            ResearchRelatedWorkErrorCode.ConcurrentModification,
                            $"{write.Message} {rollbackConflict}");
                    }
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException)
                {
                    return ResearchRelatedWorkResult.Failure(
                        ResearchRelatedWorkErrorCode.WriteFailed,
                        $"{write.Message} The new Task also could not be rolled back: {rollbackException.Message}");
                }
                return write;
            }

            var refreshed = RefreshRelatedWorkTopic(topic.Id);
            var relatedTask = refreshed.RelatedWork.ActiveTasks
                .Concat(refreshed.RelatedWork.CompletedTasks)
                .Single(task => string.Equals(
                    task.TaskId,
                    created.Id,
                    StringComparison.Ordinal));
            return ResearchRelatedWorkResult.Success(
                refreshed,
                relatedTask,
                $"Created Task '{created.Title}' and linked it to '{refreshed.Title}'.");
        }
    }

    public ResearchRelatedWorkResult LinkExistingTask(
        string topicId,
        string taskId)
    {
        lock (_gate)
        {
            if (!TryGetRelatedWorkServices(out var unavailable))
                return unavailable;
            if (!TryNormalizeTaskId(taskId, out var normalizedTaskId, out var invalid))
                return invalid;
            if (!TryGetTopicCandidate(topicId, out var topic, out var topicFailure))
                return topicFailure;

            var task = _taskIndex!.ById(normalizedTaskId);
            if (task is null)
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.TaskNotFound,
                    $"No Task with id '{normalizedTaskId}' exists in the Task Index.");
            }
            if (task.IsCancelled)
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.TaskReadOnly,
                    $"Task '{task.Title}' is cancelled and read-only. Restore it before linking new Related Work.");
            }

            var topicHasTask = topic.RelatedTaskIds.Contains(
                task.Id,
                StringComparer.OrdinalIgnoreCase);
            var taskHasTopic = HasTopicLink(task, topic);
            if (topicHasTask && taskHasTopic)
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.DuplicateRelationship,
                    $"Task '{task.Title}' is already linked to '{topic.Title}'.");
            }
            if (topicHasTask || taskHasTopic)
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.IncompleteRelationship,
                    $"Task '{task.Title}' has an incomplete reciprocal link. Use Repair link instead.");
            }

            return CompleteReciprocalLink(topic, task, "Linked");
        }
    }

    public ResearchRelatedWorkResult RepairRelatedTask(
        string topicId,
        string taskId)
    {
        lock (_gate)
        {
            if (!TryGetRelatedWorkServices(out var unavailable))
                return unavailable;
            if (!TryNormalizeTaskId(taskId, out var normalizedTaskId, out var invalid))
                return invalid;
            if (!TryGetTopicCandidate(topicId, out var topic, out var topicFailure))
                return topicFailure;

            var task = _taskIndex!.ById(normalizedTaskId);
            if (task is null)
            {
                if (!topic.RelatedTaskIds.Contains(
                        normalizedTaskId,
                        StringComparer.OrdinalIgnoreCase))
                {
                    return ResearchRelatedWorkResult.Failure(
                        ResearchRelatedWorkErrorCode.TaskNotFound,
                        $"No Task with id '{normalizedTaskId}' exists in the Task Index.");
                }

                var write = WriteRelatedTaskIds(
                    topic,
                    normalizedTaskId,
                    included: false);
                if (!write.Succeeded)
                    return write;
                var refreshed = RefreshRelatedWorkTopic(topic.Id);
                return ResearchRelatedWorkResult.Success(
                    refreshed,
                    new ResearchRelatedTask(
                        normalizedTaskId,
                        normalizedTaskId,
                        "missing",
                        ResearchTaskRelationState.MissingTask),
                    $"Removed the missing Task reference '{normalizedTaskId}' from '{topic.Title}'.");
            }

            var duplicateWarning = topic.RelatedWorkWarnings.Any(warning =>
                warning.Code == ResearchRelatedWorkWarningCode.DuplicateTaskId
                && string.Equals(
                    warning.Reference,
                    task.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (HasTopicLink(task, topic)
                && topic.RelatedTaskIds.Contains(task.Id, StringComparer.OrdinalIgnoreCase)
                && !duplicateWarning)
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.DuplicateRelationship,
                    $"Task '{task.Title}' already has a healthy reciprocal link to '{topic.Title}'.");
            }

            return CompleteReciprocalLink(topic, task, "Repaired");
        }
    }

    private ResearchRelatedWorkResult CompleteReciprocalLink(
        WikiPageCandidate topic,
        GlassworkTask task,
        string verb)
    {
        var originalTask = task.Clone();
        var needsTaskWrite = !HasTopicLink(task, topic);
        if (needsTaskWrite && task.IsCancelled)
        {
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.TaskReadOnly,
                $"Task '{task.Title}' is cancelled and read-only. Restore it before repairing its Topic reference.");
        }
        if (needsTaskWrite)
            task.RelatedLinks.Add(CreateTopicLink(topic));
        if (needsTaskWrite)
        {
            try
            {
                _taskVault!.Save(task);
                originalTask.ResourceRevision = task.ResourceRevision;
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.WriteFailed,
                    $"Task '{task.Title}' could not be updated: {ex.Message}");
            }
        }

        var write = WriteRelatedTaskIds(topic, task.Id, included: true);
        if (!write.Succeeded)
        {
            if (needsTaskWrite)
            {
                try
                {
                    _taskVault!.Save(originalTask);
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException)
                {
                    return ResearchRelatedWorkResult.Failure(
                        ResearchRelatedWorkErrorCode.WriteFailed,
                        $"{write.Message} The Task reciprocal link also could not be rolled back: {rollbackException.Message}");
                }
            }
            return write;
        }

        var refreshed = RefreshRelatedWorkTopic(topic.Id);
        var relatedTask = refreshed.RelatedWork.ActiveTasks
            .Concat(refreshed.RelatedWork.CompletedTasks)
            .Single(row => string.Equals(
                row.TaskId,
                task.Id,
                StringComparison.Ordinal));
        return ResearchRelatedWorkResult.Success(
            refreshed,
            relatedTask,
            $"{verb} Task '{task.Title}' and Research Topic '{topic.Title}'.");
    }

    private ResearchRelatedWork BuildRelatedWork(WikiPageCandidate topic)
    {
        var warnings = new List<ResearchRelatedWorkWarning>(topic.RelatedWorkWarnings);
        if (_taskIndex is null)
        {
            return new ResearchRelatedWork(
                Array.Empty<ResearchRelatedTask>(),
                Array.Empty<ResearchRelatedTask>(),
                Array.AsReadOnly(warnings.ToArray()));
        }

        var tasks = _taskIndex.All;
        var tasksById = tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        var declaredIds = topic.RelatedTaskIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reciprocalIds = tasks
            .Where(task => HasTopicLink(task, topic))
            .Select(task => task.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allIds = declaredIds
            .Concat(reciprocalIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var related = new List<ResearchRelatedTask>();
        foreach (var taskId in allIds)
        {
            if (!tasksById.TryGetValue(taskId, out var task))
            {
                related.Add(new ResearchRelatedTask(
                    taskId,
                    taskId,
                    "missing",
                    ResearchTaskRelationState.MissingTask));
                warnings.Add(new ResearchRelatedWorkWarning(
                    taskId,
                    ResearchRelatedWorkWarningCode.MissingTask,
                    $"Related Task '{taskId}' no longer exists. Repair removes the stale Topic reference.",
                    CanRepair: true));
                continue;
            }

            var isDeclared = declaredIds.Contains(task.Id);
            var hasTopicLink = reciprocalIds.Contains(task.Id);
            var state = isDeclared && hasTopicLink
                ? ResearchTaskRelationState.Healthy
                : isDeclared
                    ? ResearchTaskRelationState.MissingTaskReciprocalLink
                    : ResearchTaskRelationState.MissingTopicReciprocalLink;
            related.Add(new ResearchRelatedTask(
                task.Id,
                task.Title,
                task.Status,
                state));
            if (state == ResearchTaskRelationState.MissingTaskReciprocalLink)
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    task.Id,
                    ResearchRelatedWorkWarningCode.MissingTaskReciprocalLink,
                    task.IsCancelled
                        ? $"Task '{task.Title}' is missing its reciprocal Topic reference. Restore the cancelled Task before repairing it."
                        : $"Task '{task.Title}' is missing its reciprocal Topic reference. Repair restores it.",
                    CanRepair: !task.IsCancelled));
            }
            else if (state == ResearchTaskRelationState.MissingTopicReciprocalLink)
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    task.Id,
                    ResearchRelatedWorkWarningCode.MissingTopicReciprocalLink,
                    $"Research Topic '{topic.Title}' is missing its reciprocal Task reference. Repair restores it.",
                    CanRepair: true));
            }
        }

        var active = related
            .Where(task => task.Status is not (
                GlassworkTask.Statuses.Done
                or GlassworkTask.Statuses.Cancelled))
            .OrderBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.TaskId, StringComparer.Ordinal)
            .ToArray();
        var completed = related
            .Where(task => task.Status is
                GlassworkTask.Statuses.Done
                or GlassworkTask.Statuses.Cancelled)
            .OrderBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.TaskId, StringComparer.Ordinal)
            .ToArray();
        return new ResearchRelatedWork(
            Array.AsReadOnly(active),
            Array.AsReadOnly(completed),
            Array.AsReadOnly(warnings
                .DistinctBy(
                    warning => $"{warning.Code}:{warning.Reference}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(warning => warning.Code)
                .ThenBy(warning => warning.Reference, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
    }

    private bool TryGetRelatedWorkServices(
        out ResearchRelatedWorkResult unavailable)
    {
        if (_taskVault is not null
            && _taskIndex is not null
            && _taskService is not null)
        {
            unavailable = null!;
            return true;
        }

        unavailable = ResearchRelatedWorkResult.Failure(
            ResearchRelatedWorkErrorCode.ServicesUnavailable,
            "Related Work requires the Task Vault, Task Index, and Task Service.");
        return false;
    }

    private bool TryGetTopicCandidate(
        string topicId,
        out WikiPageCandidate topic,
        out ResearchRelatedWorkResult failure)
    {
        if (!_initialized)
            Hydrate(_today());
        topic = _pagesByPath.Values.SingleOrDefault(page =>
            page.IsOptedIn
            && string.Equals(
                page.Id,
                topicId?.Trim(),
                StringComparison.OrdinalIgnoreCase))!;
        if (topic is not null)
        {
            failure = null!;
            return true;
        }

        failure = ResearchRelatedWorkResult.Failure(
            ResearchRelatedWorkErrorCode.TopicNotFound,
            $"No opted-in Research Topic with id '{topicId?.Trim()}' exists.");
        return false;
    }

    private static bool TryNormalizeTaskId(
        string taskId,
        out string normalized,
        out ResearchRelatedWorkResult failure)
    {
        normalized = taskId?.Trim() ?? string.Empty;
        if (normalized.Length > 0 && SafeTaskIdRegex().IsMatch(normalized))
        {
            failure = null!;
            return true;
        }

        failure = ResearchRelatedWorkResult.Failure(
            ResearchRelatedWorkErrorCode.InvalidTaskId,
            "Enter a valid Task ID using lowercase letters, numbers, and hyphens.");
        return false;
    }

    private ResearchTopic RefreshRelatedWorkTopic(string topicId)
    {
        Hydrate(_today());
        return _snapshot.Topics.Single(topic => string.Equals(
            topic.Id,
            topicId,
            StringComparison.OrdinalIgnoreCase));
    }

    private ResearchRelatedWorkResult WriteRelatedTaskIds(
        WikiPageCandidate topic,
        string taskId,
        bool included)
    {
        var fullPath = Path.Combine(
            _vaultRoot,
            topic.VaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] originalBytes;
        string original;
        TextEncodingInfo encoding;
        try
        {
            originalBytes = File.ReadAllBytes(fullPath);
            original = DecodeText(originalBytes, out encoding);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or DecoderFallbackException)
        {
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.WriteFailed,
                $"Research Topic '{topic.Title}' could not be read for update: {ex.Message}");
        }

        var match = FrontmatterRegex().Match(original);
        if (!match.Success)
        {
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.InvalidResearchMetadata,
                $"Research Topic '{topic.Title}' has no complete YAML frontmatter block.");
        }
        WikiPageFrontmatter? frontmatter;
        try
        {
            frontmatter = YamlDeserializer.Deserialize<WikiPageFrontmatter>(
                match.Groups[1].Value);
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.InvalidResearchMetadata,
                $"Research Topic '{topic.Title}' has malformed frontmatter: {ex.Message}");
        }
        if (!string.Equals(
                frontmatter?.Id?.Trim(),
                topic.Id,
                StringComparison.OrdinalIgnoreCase)
            || !ContainsResearchMetadata(match.Groups[1].Value))
        {
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.ConcurrentModification,
                $"Research Topic '{topic.Title}' changed identity or Research membership before Related Work was updated.");
        }

        var metadata = ParseResearchMetadata(match.Groups[1].Value, topic.Id);
        if (metadata.RelatedWorkWarnings.Any(warning =>
                warning.Code is ResearchRelatedWorkWarningCode.InvalidMetadata
                    or ResearchRelatedWorkWarningCode.InvalidTaskId))
        {
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.InvalidResearchMetadata,
                $"Research Topic '{topic.Title}' has malformed Related Work metadata. Repair it in Obsidian before retrying.");
        }
        var currentIds = metadata.RelatedTaskIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (included)
            currentIds.Add(taskId);
        else
            currentIds.Remove(taskId);
        var orderedIds = currentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!TrySetResearchOverrideList(
                match.Groups[1].Value,
                "related_work",
                orderedIds,
                out var updatedYaml))
        {
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.InvalidResearchMetadata,
                $"Research Topic '{topic.Title}' has Related Work metadata that cannot be updated safely.");
        }
        var updated = original[..match.Groups[1].Index]
            + updatedYaml
            + original[(match.Groups[1].Index + match.Groups[1].Length)..];
        var updatedBytes = EncodeText(updated, encoding);
        var tempPath = fullPath + ".related-work-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = fullPath + ".related-work-" + Guid.NewGuid().ToString("N") + ".bak";
        var replacementApplied = false;
        var preserveTemp = false;
        var preserveBackup = false;
        try
        {
            using (var temp = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                temp.Write(updatedBytes);
                temp.Flush(flushToDisk: true);
            }
            var currentBytes = File.ReadAllBytes(fullPath);
            if (!currentBytes.AsSpan().SequenceEqual(originalBytes))
            {
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.ConcurrentModification,
                    $"Research Topic '{topic.Title}' changed while Related Work was being updated. Review the latest file and retry.");
            }

            BeforeRelatedWorkFileReplaceHook?.Invoke();
            _selfWrites?.RegisterWrite(fullPath);
            File.Replace(tempPath, fullPath, backupPath);
            replacementApplied = true;
            var displacedBytes = File.ReadAllBytes(backupPath);
            if (!displacedBytes.AsSpan().SequenceEqual(originalBytes))
            {
                if (!TryRestoreOptInBackup(
                        fullPath,
                        backupPath,
                        updatedBytes,
                        out var rollbackError))
                {
                    preserveBackup = true;
                    return ResearchRelatedWorkResult.Failure(
                        ResearchRelatedWorkErrorCode.WriteFailed,
                        $"Research Topic '{topic.Title}' changed during the atomic Related Work update and newer content could not be safely restored. Recovery copy preserved at '{backupPath}': {rollbackError}");
                }
                replacementApplied = false;
                return ResearchRelatedWorkResult.Failure(
                    ResearchRelatedWorkErrorCode.ConcurrentModification,
                    $"Research Topic '{topic.Title}' changed during the atomic Related Work update. The newer external content was restored.");
            }
            return new ResearchRelatedWorkResult(
                true,
                null,
                null,
                null,
                $"Updated Related Work for '{topic.Title}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (replacementApplied && File.Exists(backupPath))
            {
                if (!TryRestoreOptInBackup(
                        fullPath,
                        backupPath,
                        updatedBytes,
                        out var rollbackError))
                {
                    preserveBackup = true;
                    return ResearchRelatedWorkResult.Failure(
                        ResearchRelatedWorkErrorCode.WriteFailed,
                        $"Research Topic '{topic.Title}' could not finish its Related Work update or safely restore displaced content. Recovery copy preserved at '{backupPath}': {rollbackError}");
                }
                replacementApplied = false;
            }
            else if (!replacementApplied)
            {
                preserveTemp = File.Exists(tempPath);
                preserveBackup = File.Exists(backupPath);
                if (preserveTemp || preserveBackup || !File.Exists(fullPath))
                {
                    return ResearchRelatedWorkResult.Failure(
                        ResearchRelatedWorkErrorCode.WriteFailed,
                        $"Research Topic '{topic.Title}' encountered an ambiguous Related Work replace failure. Live exists: {File.Exists(fullPath)}; replacement preserved: {preserveTemp}; backup preserved: {preserveBackup}. Inspect recovery files before retrying: {ex.Message}");
                }
            }
            return ResearchRelatedWorkResult.Failure(
                ResearchRelatedWorkErrorCode.WriteFailed,
                $"Research Topic '{topic.Title}' could not update Related Work: {ex.Message}");
        }
        finally
        {
            if (!preserveTemp)
                TryDeleteRecoveryFile(tempPath);
            if (!preserveBackup)
                TryDeleteRecoveryFile(backupPath);
        }
    }

    private static RelatedLink CreateTopicLink(WikiPageCandidate topic) =>
        new()
        {
            Slug = TopicLinkSlug(topic),
            DisplayName = topic.Title,
        };

    private static bool HasTopicLink(GlassworkTask task, WikiPageCandidate topic)
    {
        var expected = TopicLinkSlug(topic);
        return task.RelatedLinks.Any(link => string.Equals(
            NormalizeReference(link.Slug),
            expected,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string TopicLinkSlug(WikiPageCandidate topic)
    {
        var path = NormalizeReference(topic.VaultRelativePath);
        return path.StartsWith("wiki/", StringComparison.OrdinalIgnoreCase)
            ? path["wiki/".Length..]
            : path;
    }
}
