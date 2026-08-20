using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public enum ResourceMutationFailurePoint
{
    BeforeJournal,
    BeforeFinalValidation,
    DuringReplacement,
    AfterReplacementBeforeCommit,
    AfterCommit,
    DuringRecovery
}

public interface IResourceMutationFaultInjector
{
    void ThrowIfInjected(ResourceMutationFailurePoint point);
}

public sealed record ResourceMutationSubtaskSnapshot(
    string Text,
    bool IsCompleted,
    string? Status,
    string? Size,
    IReadOnlyDictionary<string, string> Metadata,
    string Notes);

public sealed record ResourceMutationTaskSnapshot(
    string Id,
    string Title,
    string Status,
    string Priority,
    string Type,
    string? Size,
    DateTime Created,
    DateTime? Due,
    DateTime? Start,
    DateTime? MyDay,
    DateTime? DeferUntil,
    string? Parent,
    string Description,
    string Notes,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> BlockedBy,
    DateTime? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancellationReason,
    string? BlockedReason,
    string ResourceRevision,
    IReadOnlyList<ResourceMutationSubtaskSnapshot>? Subtasks = null);

public sealed record ResourceMutationOutcome(
    string MutationId,
    string Outcome,
    bool Replayed,
    string? ExpectedRevision,
    string? CurrentRevision,
    ResourceMutationTaskSnapshot? Task,
    string? Error = null,
    IReadOnlyList<ResourceMutationTaskSnapshot>? Tasks = null,
    IReadOnlyList<ResourceMutationDiagnostic>? Diagnostics = null,
    TaskDeletionPreflight? DeletionPreflight = null,
    TaskDeletionReport? DeletionReport = null);

public sealed record ResourceMutationDiagnostic(
    string Code,
    int OperationIndex,
    IReadOnlyList<string> TaskIds,
    string Message);

/// <summary>
/// Durable, conditional single-resource mutation boundary.
/// </summary>
public sealed partial class ResourceMutationService
{
    private const int RetentionDays = 30;
    private readonly string _vaultPath;
    private readonly VaultService _vault;
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IResourceMutationFaultInjector? _faults;
    private readonly FrontmatterParser _parser = new();
    private readonly HashSet<string> _recoveredDeletes = new(StringComparer.Ordinal);
    private readonly object _recoveredDeletesGate = new();

    public ResourceMutationService(
        string vaultPath,
        VaultService? vault = null,
        Func<DateTimeOffset>? clock = null,
        IResourceMutationFaultInjector? faults = null,
        IBacklinkIndex? backlinkIndex = null)
    {
        _vaultPath = vaultPath;
        _vault = vault ?? new VaultService(vaultPath);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _faults = faults;
        _backlinkIndex = backlinkIndex;
        _statePath = Path.Combine(vaultPath, ".glasswork", "resource-mutations.json");
        _vault.RegisterManagedRecovery(RecoverWithExclusiveLease);
        _vault.RegisterManagedDeleteRecovery(DrainRecoveredDeletes);
        _vault.RunManagedRecovery();
        _vault.AttachMutationService(this);
    }

    internal void CommitTask(GlassworkTask task, bool ifAbsent = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new ArgumentException("Task must have an ID before saving.", nameof(task));

        var notifications = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                notifications.UnionWith(RecoverUnsafe());
                var updated = Encoding.UTF8.GetBytes(_parser.Serialize(task));
                CommitBytesUnsafe(
                    task.Id,
                    updated,
                    notifications,
                    expectedOriginal: null,
                    expectedRevision: task.ResourceRevision,
                    ifAbsent: ifAbsent);
                task.ResourceRevision = Revision(updated);
            }
        }
        catch
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
                notifications.UnionWith(RecoverUnsafe());
            throw;
        }
        finally
        {
            NotifyRecoveredDeletes();
            foreach (var taskId in notifications)
                _vault.NotifyTaskWritten(taskId);
        }
    }

    internal void CommitBytes(string taskId, byte[] updated, byte[]? expectedOriginal = null)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID is required.", nameof(taskId));
        ArgumentNullException.ThrowIfNull(updated);

        var notifications = new HashSet<string>(StringComparer.Ordinal);
        var recoveredWrites = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                recoveredWrites.UnionWith(RecoverUnsafe());
                CommitBytesUnsafe(taskId, updated, notifications, expectedOriginal);
            }

        }
        catch
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
                recoveredWrites.UnionWith(RecoverUnsafe());
            throw;
        }
        finally
        {
            NotifyRecoveredDeletes();
            foreach (var id in recoveredWrites)
                _vault.NotifyTaskWritten(id);
            foreach (var id in notifications)
                _vault.NotifyTaskWritten(id);
        }
    }

    internal bool CommitDelete(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID is required.", nameof(taskId));

        var notifications = new HashSet<string>(StringComparer.Ordinal);
        var recoveredWrites = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                recoveredWrites.UnionWith(RecoverUnsafe());
                var original = _vault.TryReadBytesUnsafe(taskId);
                if (original is null)
                    return false;

                var journal = new JournalEntry(
                    taskId,
                    Convert.ToBase64String(original),
                    string.Empty,
                    $"app-delete-{Guid.NewGuid():N}",
                    Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(),
                    Revision(original),
                    Committed: false,
                    Existed: true,
                    Deleted: true);
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeJournal);
                WriteJournal(journal);
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
                _vault.DeleteUnsafe(taskId);
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterReplacementBeforeCommit);
                WriteJournal(journal with { Committed = true });
                _vault.ForgetManagedBytes(taskId);
                notifications.Add(taskId);
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterCommit);
                DeleteJournal();
                return true;
            }

        }
        catch
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
                recoveredWrites.UnionWith(RecoverUnsafe());
            throw;
        }

        finally
        {
            NotifyRecoveredDeletes(notifications);
            foreach (var id in recoveredWrites)
                _vault.NotifyTaskWritten(id);
            foreach (var id in notifications)
                _vault.NotifyTaskDeleted(id);
        }

    }

    public bool CommitTaskOwnedFile(string path, byte[] content, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(content);
        using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
        {
            RecoverUnsafe();
            var original = _vault.TryReadOwnedBytesUnsafe(path);
            if (!overwrite && original is not null)
                return false;
            var journal = new JournalEntry(
                string.Empty,
                original is null ? null : Convert.ToBase64String(original),
                Convert.ToBase64String(content),
                $"app-file-{Guid.NewGuid():N}",
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                null,
                Committed: false,
                Existed: original is not null,
                OwnedPath: path);
            WriteJournal(journal);
            _vault.ReplaceOwnedFileUnsafe(path, content, overwrite: true);
            WriteJournal(journal with { Committed = true });
            DeleteJournal();
            return true;
        }
    }

    public ResourceMutationOutcome CommitTaskOwnedFileConditional(
            string path,
            byte[] content,
            bool overwrite,
            string? mutationId,
            string? expectedRevision,
            bool? ifAbsent)
        {
            ArgumentNullException.ThrowIfNull(content);
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                RecoverUnsafe();
                var current = _vault.TryReadOwnedBytesUnsafe(path);
                var hash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{path}\n{overwrite}\n{expectedRevision}\n{ifAbsent}\n{Convert.ToBase64String(content)}")))
                    .ToLowerInvariant();
                var state = ReadState();
                Prune(state);

                if (string.IsNullOrWhiteSpace(mutationId)
                    || (overwrite && current is not null && string.IsNullOrWhiteSpace(expectedRevision))
                    || (!overwrite && ifAbsent != true))
                    return new ResourceMutationOutcome(
                        mutationId ?? string.Empty, "precondition_required", false, expectedRevision,
                        current is null ? null : Revision(current), null,
                        "mutation_id and the applicable artifact precondition are required.");

                if (state.Outcomes.TryGetValue(mutationId, out var recorded))
                {
                    if (!string.Equals(recorded.RequestHash, hash, StringComparison.Ordinal))
                        return new ResourceMutationOutcome(
                            mutationId, "mutation_id_reused", false, expectedRevision, null, null,
                            "mutation_id was already used for a different request.");
                    return recorded.Outcome with { Replayed = true };
                }

                var currentRevision = current is null ? null : Revision(current);
                if (!overwrite && current is not null)
                    return Record(state, mutationId, hash, new ResourceMutationOutcome(
                        mutationId, "conflict", false, null, currentRevision, null,
                        "Artifact already exists."));
                if (overwrite && expectedRevision is not null
                    && !string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal))
                    return Record(state, mutationId, hash, new ResourceMutationOutcome(
                        mutationId, "conflict", false, expectedRevision, currentRevision, null,
                        "Artifact changed before commit."));
                if (current is not null && current.AsSpan().SequenceEqual(content))
                    return Record(state, mutationId, hash, new ResourceMutationOutcome(
                        mutationId, "no_op", false, expectedRevision, currentRevision, null));

                var journal = new JournalEntry(
                    string.Empty,
                    current is null ? null : Convert.ToBase64String(current),
                    Convert.ToBase64String(content),
                    mutationId,
                    hash,
                    expectedRevision,
                    Committed: false,
                    Existed: current is not null,
                    OwnedPath: path);
                WriteJournal(journal);
                try
                {
                    _vault.ReplaceOwnedFileUnsafe(path, content, overwrite: true);
                    WriteJournal(journal with { Committed = true });
                    var outcome = Record(state, mutationId, hash, new ResourceMutationOutcome(
                        mutationId, "applied", false, expectedRevision, Revision(content), null));
                    DeleteJournal();
                    return outcome;
                }
                catch
                {
                    RecoverUnsafe();
                    throw;
                }
            }
        }

    private void CommitBytesUnsafe(
        string taskId,
        byte[] updated,
        ISet<string> notifications,
        byte[]? expectedOriginal,
        string? expectedRevision = null,
        bool ifAbsent = false)
    {
        var original = _vault.TryReadBytesUnsafe(taskId);
        if (ifAbsent && original is not null)
            throw new InvalidOperationException("if_absent is required for a new Task.");
        var expected = expectedOriginal ?? original;
        if (expectedRevision is not null
            && (original is null || !string.Equals(Revision(original), expectedRevision, StringComparison.Ordinal)))
            throw new ResourceRevisionConflictException("Task changed before commit.");
        if ((expected is null) != (original is null)
            || (expected is not null && original is not null && !expected.AsSpan().SequenceEqual(original)))
            throw new ResourceRevisionConflictException("Task changed before commit.");

        if (original is not null && original.AsSpan().SequenceEqual(updated))
            return;

        var mutationId = $"app-{Guid.NewGuid():N}";
        var journalExpectedRevision = original is null ? null : Revision(original);
        var requestHash = Convert.ToHexString(SHA256.HashData(updated)).ToLowerInvariant();
        var journal = new JournalEntry(
            taskId,
            original is null ? null : Convert.ToBase64String(original),
            Convert.ToBase64String(updated),
            mutationId,
            requestHash,
            journalExpectedRevision,
            Committed: false,
            Existed: original is not null);

        var committed = false;
        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeFinalValidation);
        var current = _vault.TryReadBytesUnsafe(taskId);
        if ((expected is null) != (current is null)
            || (expected is not null && current is not null && !expected.AsSpan().SequenceEqual(current)))
            throw new ResourceRevisionConflictException("Task changed before commit.");

        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeJournal);
        WriteJournal(journal);
        try
        {
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
            _vault.ReplaceBytesUnsafe(taskId, updated);
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterReplacementBeforeCommit);
            WriteJournal(journal with { Committed = true });
            committed = true;
            _vault.RememberManagedBytes(taskId, updated);
            notifications.Add(taskId);
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterCommit);
            DeleteJournal();
        }
        catch
        {
            var recovered = RecoverUnsafe();
            if (committed)
                notifications.UnionWith(recovered);
            throw;
        }
    }

    public ResourceMutationOutcome TransactSingleTask(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        JsonElement fields)
    {
        var notifications = new HashSet<string>(StringComparer.Ordinal);
        ResourceMutationOutcome? result = null;

        try
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                notifications.UnionWith(RecoverUnsafe());
                result = TransactSingleTaskUnsafe(
                    mutationId,
                    taskId,
                    ifRevision,
                    "set_task_fields",
                    fields,
                    task => ApplyFields(task, fields),
                    notifications);
            }
        }
        catch
        {
            try
            {
                using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
                    notifications.UnionWith(RecoverUnsafe());
            }
            catch
            {
                // Preserve the original failure; the next managed access retries recovery.
            }

            throw;
        }
        finally
        {
            foreach (var taskIdToNotify in notifications)
                _vault.NotifyTaskWritten(taskIdToNotify);
        }

        return result!;
    }

    public ResourceMutationOutcome CancelTask(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        string? reason)
    {
        var payload = JsonSerializer.SerializeToElement(new { reason });
        return TransactTaskLifecycle(
            mutationId,
            taskId,
            ifRevision,
            "cancel_task",
            payload,
            task =>
            {
                try
                {
                    TaskService.ApplyCancel(task, reason ?? string.Empty, _clock);
                    return null;
                }
                catch (ArgumentException ex)
                {
                    return ex.Message;
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message;
                }
            });
    }

    public ResourceMutationOutcome RestoreTask(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        string restoreStatus = GlassworkTask.Statuses.Todo)
    {
        var payload = JsonSerializer.SerializeToElement(new { restore_status = restoreStatus });
        return TransactTaskLifecycle(
            mutationId,
            taskId,
            ifRevision,
            "restore_task",
            payload,
            task =>
            {
                try
                {
                    TaskService.ApplyRestoreCancelled(task, restoreStatus);
                    return null;
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message;
                }
            });
    }

    public ResourceMutationOutcome ReconcileAdoTask(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        int? adoWorkItemId,
        string? authoritativeState)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            ado_work_item_id = adoWorkItemId,
            authoritative_state = authoritativeState,
        });
        return TransactTaskLifecycle(
            mutationId,
            taskId,
            ifRevision,
            "reconcile_ado_task",
            payload,
            task =>
            {
                if (adoWorkItemId is null or <= 0)
                    return "ado_work_item_id must be a positive integer.";
                if (string.IsNullOrWhiteSpace(authoritativeState))
                    return "authoritative_state is required.";

                if (!RepresentsAdoWorkItem(task, adoWorkItemId.Value))
                    return $"Task does not resolve to ADO work item {adoWorkItemId}.";

                if (string.Equals(authoritativeState, "Removed", StringComparison.Ordinal)
                    && task.Status is (
                        GlassworkTask.Statuses.Todo
                        or GlassworkTask.Statuses.InProgress
                        or GlassworkTask.Statuses.Blocked))
                {
                    TaskService.ApplyCancel(task, "ADO work item removed", _clock);
                }
                else if (authoritativeState is "Active" or "In Progress" or "In Review"
                         && task.IsCancelled)
                {
                    TaskService.ApplyRestoreCancelled(
                        task,
                        GlassworkTask.Statuses.InProgress);
                }

                return null;
            });
    }

    private bool RepresentsAdoWorkItem(GlassworkTask task, int adoWorkItemId)
    {
        var adoLinks = task.Links
            .Where(link => string.Equals(link.Type, TaskLink.Types.Ado, StringComparison.Ordinal))
            .ToList();
        if (adoLinks.Count > 0)
        {
            var resolved = adoLinks
                .Select(link => AdoParentIdExtractor.TryExtractId(link.Value))
                .ToList();
            return resolved.All(id => id.HasValue)
                   && resolved.Distinct().Count() == 1
                   && resolved[0] == adoWorkItemId;
        }

        var ado = TaskTypeBackfillService.ResolveAdoId(_parser.Serialize(task));
        return ado.Status == AdoIdStatus.Resolved && ado.Id == adoWorkItemId;
    }

    private ResourceMutationOutcome TransactTaskLifecycle(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        string operation,
        JsonElement payload,
        Func<GlassworkTask, string?> apply)
    {
        var notifications = new HashSet<string>(StringComparer.Ordinal);
        ResourceMutationOutcome? result = null;

        try
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                notifications.UnionWith(RecoverUnsafe());
                result = TransactSingleTaskUnsafe(
                    mutationId,
                    taskId,
                    ifRevision,
                    operation,
                    payload,
                    apply,
                    notifications);
            }
        }
        catch
        {
            try
            {
                using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
                    notifications.UnionWith(RecoverUnsafe());
            }
            catch
            {
                // Preserve the original failure; the next managed access retries recovery.
            }
            throw;
        }
        finally
        {
            foreach (var taskIdToNotify in notifications)
                _vault.NotifyTaskWritten(taskIdToNotify);
        }

        return result!;
    }

    private ResourceMutationOutcome TransactSingleTaskUnsafe(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        string operation,
        JsonElement payload,
        Func<GlassworkTask, string?> apply,
        ISet<string> notifications)
    {
        var state = ReadState();
        Prune(state);
        var requestHash = HashRequest(mutationId, operation, taskId, ifRevision, payload);

        if (string.IsNullOrWhiteSpace(mutationId))
            return new ResourceMutationOutcome(
                string.Empty, "precondition_required", false, ifRevision, null, null,
                "mutation_id is required.");

        if (state.Outcomes.TryGetValue(mutationId, out var recorded))
        {
            if (recorded.RequestHash != requestHash)
                return new ResourceMutationOutcome(
                    mutationId, "mutation_id_reused", false, ifRevision, null, null,
                    "mutation_id was already used for a different request.");
            return recorded.Outcome with { Replayed = true };
        }

        if (string.IsNullOrWhiteSpace(ifRevision))
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "precondition_required", false, ifRevision, null, null,
                    "if_revision is required."));

        if (string.IsNullOrWhiteSpace(taskId))
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "validation_error", false, ifRevision, null, null,
                    "task_id is required."));

        if (payload.ValueKind != JsonValueKind.Object)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "validation_error", false, ifRevision, null, null,
                    "fields must be a JSON object."));

        var bytes = _vault.TryReadBytesUnsafe(taskId);
        if (bytes is null)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "not_found", false, ifRevision, null, null,
                    "Task was not found."));

        var currentRevision = Revision(bytes);
        var currentTask = _parser.Parse(Encoding.UTF8.GetString(bytes));
        if (!string.Equals(ifRevision, currentRevision, StringComparison.Ordinal))
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "conflict", false, ifRevision, currentRevision,
                    Snapshot(currentTask, currentRevision),
                    "if_revision does not match the current Resource Revision."));

        var staged = _parser.Parse(Encoding.UTF8.GetString(bytes));
        string? error;
        try
        {
            error = apply(staged);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }

        if (error is not null)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "validation_error", false, ifRevision, currentRevision, null, error));

        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeFinalValidation);
        var finalBytes = _vault.TryReadBytesUnsafe(taskId);
        if (finalBytes is null)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "not_found", false, ifRevision, null, null,
                    "Task was removed before commit."));

        var finalRevision = Revision(finalBytes);
        var finalTask = _parser.Parse(Encoding.UTF8.GetString(finalBytes));
        if (!string.Equals(finalRevision, currentRevision, StringComparison.Ordinal))
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "conflict", false, ifRevision, finalRevision,
                    Snapshot(finalTask, finalRevision), "Task changed before commit."));

        if (SemanticallyEqual(finalTask, staged))
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "no_op", false, ifRevision, finalRevision,
                    Snapshot(finalTask, finalRevision)));

        var updatedBytes = Encoding.UTF8.GetBytes(_parser.Serialize(staged));
        var journal = new JournalEntry(
            taskId,
            Convert.ToBase64String(finalBytes),
            Convert.ToBase64String(updatedBytes),
            mutationId,
            requestHash,
            ifRevision,
            Committed: false);
        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeJournal);
        WriteJournal(journal);

        var replacementBytes = _vault.TryReadBytesUnsafe(taskId);
        if (replacementBytes is null)
        {
            DeleteJournal();
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "not_found", false, ifRevision, null, null,
                    "Task was removed before replacement."));
        }

        var replacementRevision = Revision(replacementBytes);
        if (!string.Equals(replacementRevision, finalRevision, StringComparison.Ordinal))
        {
            DeleteJournal();
            var externallyChanged = _parser.Parse(Encoding.UTF8.GetString(replacementBytes));
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "conflict", false, ifRevision, replacementRevision,
                    Snapshot(externallyChanged, replacementRevision),
                    "Task changed immediately before replacement."));
        }

        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
        _vault.ReplaceBytesUnsafe(taskId, updatedBytes);
        notifications.Add(taskId);
        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterReplacementBeforeCommit);
        WriteJournal(journal with { Committed = true });
        var newRevision = Revision(updatedBytes);
        var applied = new ResourceMutationOutcome(
            mutationId, "applied", false, ifRevision, newRevision,
            Snapshot(staged, newRevision));
        var recordedOutcome = Record(state, mutationId, requestHash, applied);
        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterCommit);
        DeleteJournal();
        return recordedOutcome;
    }

    public ResourceMutationOutcome TransactTasks(
        string? mutationId,
        JsonElement operations,
        string? transactionRevision = null,
        JsonElement? assertions = null)
    {
        var notifications = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                notifications.UnionWith(RecoverUnsafe());
                var result = TransactTasksUnsafe(mutationId, operations, transactionRevision, assertions, notifications);
                return result;
            }
        }
        catch
        {
            try
            {
                using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
                    notifications.UnionWith(RecoverUnsafe());
            }
            catch
            {
                // Preserve the original failure; the next managed access retries recovery.
            }

            throw;
        }
        finally
        {
            foreach (var taskIdToNotify in notifications)
                _vault.NotifyTaskWritten(taskIdToNotify);
        }
    }

    private ResourceMutationOutcome TransactTasksUnsafe(
        string? mutationId,
        JsonElement operations,
        string? transactionRevision,
        JsonElement? assertions,
        ISet<string> notifications)
    {
        var state = ReadState();
        Prune(state);
        var requestHash = HashTransactionRequest(mutationId, transactionRevision, operations, assertions);

        if (string.IsNullOrWhiteSpace(mutationId))
            return new ResourceMutationOutcome(
                string.Empty, "precondition_required", false, transactionRevision, null, null,
                "mutation_id is required.");

        if (state.Outcomes.TryGetValue(mutationId, out var recorded))
        {
            if (recorded.RequestHash != requestHash)
                return new ResourceMutationOutcome(
                    mutationId, "mutation_id_reused", false, transactionRevision, null, null,
                    "mutation_id was already used for a different request.");
            return recorded.Outcome with { Replayed = true };
        }

        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() == 0)
            return Record(state, mutationId, requestHash, TransactionError(
                mutationId, "validation_error", "operations must be a non-empty JSON array.",
                new ResourceMutationDiagnostic("invalid_operations", -1, [], "operations must be a non-empty JSON array.")));

        var staged = new Dictionary<string, StagedTask>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var bytes = File.ReadAllBytes(path);
            staged[id] = new StagedTask(_parser.Parse(Encoding.UTF8.GetString(bytes)), bytes, false);
        }

        var diagnostics = new List<ResourceMutationDiagnostic>();
        var touchedOperationIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (assertions is { } assertionArray)
        {
            if (assertionArray.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new("invalid_assertions", -1, [], "assertions must be a JSON array."));
            }
            else
            {
                for (var index = 0; index < assertionArray.GetArrayLength(); index++)
                {
                    var assertion = assertionArray[index];
                    var taskId = ReadOptionalTaskId(assertion);
                    ApplyRevisionAssertion(staged, assertion, taskId, index, diagnostics);
                }
            }
        }
        for (var index = 0; index < operations.GetArrayLength(); index++)
        {
            var operation = operations[index];
            if (operation.ValueKind != JsonValueKind.Object
                || !operation.TryGetProperty("op", out var opElement)
                || opElement.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new("invalid_operation", index, [], "Operation must contain a string op."));
                continue;
            }

            var op = opElement.GetString()!;
            var taskId = ReadOptionalTaskId(operation);
            if (taskId is not null)
                touchedOperationIndexes.TryAdd(taskId, index);
            if (op is not "assert_task_revision" and not "assert_revision"
                && string.IsNullOrWhiteSpace(taskId))
            {
                diagnostics.Add(new("task_id_required", index, [], "Operation requires task_id."));
                continue;
            }

            switch (op)
            {
                case "assert_task_revision":
                case "assert_revision":
                    ApplyRevisionAssertion(staged, operation, taskId, index, diagnostics);
                    break;
                case "set_task_fields":
                    ApplyStagedFields(staged, operation, taskId!, transactionRevision, index, diagnostics);
                    break;
                case "create_task":
                    CreateStagedTask(staged, operation, taskId!, index, diagnostics);
                    break;
                case "replace_task_relationships":
                    ReplaceStagedRelationships(staged, operation, taskId!, index, diagnostics);
                    break;
                default:
                    diagnostics.Add(new("unsupported_operation", index, taskId is null ? [] : [taskId],
                        $"Unsupported transaction operation '{op}'."));
                    break;
            }
        }

        diagnostics.AddRange(ValidateStagedGraph(staged, touchedOperationIndexes));
        if (diagnostics.Count > 0)
        {
            var outcome = diagnostics.Any(diagnostic => diagnostic.Code == "conflict")
                ? "conflict"
                : diagnostics.Any(diagnostic => diagnostic.Code == "precondition_required")
                    ? "precondition_required"
                    : "validation_error";
            var implicatedTask = diagnostics
                .SelectMany(diagnostic => diagnostic.TaskIds)
                .Select(id => staged.TryGetValue(id, out var value)
                    ? Snapshot(value.Task, Revision(value.OriginalBytes))
                    : null)
                .FirstOrDefault(snapshot => snapshot is not null);
            return Record(state, mutationId, requestHash, new ResourceMutationOutcome(
                mutationId,
                outcome,
                false,
                transactionRevision,
                implicatedTask?.ResourceRevision,
                implicatedTask,
                "Transaction validation failed.",
                Diagnostics: diagnostics));
        }

        var changed = staged.Values
            .Where(value => value.Created || !SemanticallyEqual(
                _parser.Parse(Encoding.UTF8.GetString(value.OriginalBytes)), value.Task))
            .ToDictionary(value => value.Task.Id, value => value, StringComparer.Ordinal);
        if (changed.Count == 0)
        {
            var snapshots = staged.Values
                .OrderBy(value => value.Task.Id, StringComparer.Ordinal)
                .Select(value => Snapshot(value.Task, Revision(value.OriginalBytes)))
                .ToArray();
            return Record(state, mutationId, requestHash, new ResourceMutationOutcome(
                mutationId, "no_op", false, transactionRevision, null, snapshots.FirstOrDefault(),
                Tasks: snapshots));
        }

        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeFinalValidation);
        foreach (var value in changed.Values)
        {
            var currentBytes = _vault.TryReadBytesUnsafe(value.Task.Id);
            if ((currentBytes is null && !value.Created)
                || (currentBytes is not null && !value.Created
                    && !currentBytes.AsSpan().SequenceEqual(value.OriginalBytes)))
            {
                var currentTask = currentBytes is null
                    ? null
                    : _parser.Parse(Encoding.UTF8.GetString(currentBytes));
                var diagnostic = new ResourceMutationDiagnostic(
                    "conflict",
                    touchedOperationIndexes.GetValueOrDefault(value.Task.Id, 0),
                    [value.Task.Id],
                    "Task changed before commit.");
                var currentRevision = currentBytes is null ? null : Revision(currentBytes);
                return Record(state, mutationId, requestHash, new ResourceMutationOutcome(
                    mutationId,
                    "conflict",
                    false,
                    transactionRevision,
                    currentRevision,
                    currentTask is null ? null : Snapshot(currentTask, currentRevision!),
                    "Task changed before commit.",
                    Diagnostics: [diagnostic]));
            }
        }
        var journalEntries = changed.Values.Select(value => new GraphJournalTaskEntry(
            value.Task.Id,
            value.Created ? null : Convert.ToBase64String(value.OriginalBytes),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(_parser.Serialize(value.Task))),
            value.Created)).ToArray();
        var journal = new GraphJournalEntry(
            mutationId,
            requestHash,
            transactionRevision,
            false,
            journalEntries);
        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeJournal);
        WriteGraphJournal(journal);

        try
        {
            foreach (var entry in journalEntries)
            {
                _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
                _vault.ReplaceBytesUnsafe(entry.TaskId, Convert.FromBase64String(entry.Updated));
                notifications.Add(entry.TaskId);
            }

            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterReplacementBeforeCommit);
            WriteGraphJournal(journal with { Committed = true });
            var snapshots = changed.Values
                .OrderBy(value => value.Task.Id, StringComparer.Ordinal)
                .Select(value =>
                {
                    var bytes = Encoding.UTF8.GetBytes(_parser.Serialize(value.Task));
                    return Snapshot(value.Task, Revision(bytes));
                })
                .ToArray();
            var applied = new ResourceMutationOutcome(
                mutationId, "applied", false, transactionRevision, null, snapshots.FirstOrDefault(),
                Tasks: snapshots);
            var recordedOutcome = Record(state, mutationId, requestHash, applied);
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterCommit);
            DeleteJournal();
            return recordedOutcome;
        }
        catch
        {
            RecoverUnsafe();
            throw;
        }
    }

    private static string? ReadOptionalTaskId(JsonElement operation)
    {
        return operation.TryGetProperty("task_id", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private void ApplyRevisionAssertion(
        IReadOnlyDictionary<string, StagedTask> staged,
        JsonElement operation,
        string? taskId,
        int operationIndex,
        ICollection<ResourceMutationDiagnostic> diagnostics)
    {
        var revision = ReadRevision(operation, operationIndex, taskId, diagnostics);
        if (taskId is null || revision is null) return;
        if (!staged.TryGetValue(taskId, out var current))
        {
            diagnostics.Add(new("task_not_found", operationIndex, [taskId], "Task was not found."));
            return;
        }

        var currentRevision = Revision(current.OriginalBytes);
        if (!string.Equals(currentRevision, revision, StringComparison.Ordinal))
            diagnostics.Add(new("conflict", operationIndex, [taskId],
                "Read-only Task Revision assertion does not match the current Resource Revision."));
    }

    private void ApplyStagedFields(
        IDictionary<string, StagedTask> staged,
        JsonElement operation,
        string taskId,
        string? transactionRevision,
        int operationIndex,
        ICollection<ResourceMutationDiagnostic> diagnostics)
    {
        if (!staged.TryGetValue(taskId, out var current))
        {
            diagnostics.Add(new("task_not_found", operationIndex, [taskId], "Task was not found."));
            return;
        }

        var revision = ReadRevision(operation, operationIndex, taskId, diagnostics);
        if (revision is not null
            && transactionRevision is not null
            && !string.Equals(revision, transactionRevision, StringComparison.Ordinal))
        {
            diagnostics.Add(new("contradictory_precondition", operationIndex, [taskId],
                "Transaction and operation revisions must match."));
            return;
        }
        if (revision is null) revision = transactionRevision;
        if (revision is not null
            && !string.Equals(revision, Revision(current.OriginalBytes), StringComparison.Ordinal))
        {
            diagnostics.Add(new("conflict", operationIndex, [taskId],
                "if_revision does not match the original Resource Revision."));
            return;
        }

        if (!operation.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new("fields_required", operationIndex, [taskId], "fields must be a JSON object."));
            return;
        }

        try
        {
            var error = ApplyFields(current.Task, fields);
            if (error is not null)
                diagnostics.Add(new("invalid_fields", operationIndex, [taskId], error));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(new("invalid_fields", operationIndex, [taskId], ex.Message));
        }
    }

    private void CreateStagedTask(
        IDictionary<string, StagedTask> staged,
        JsonElement operation,
        string taskId,
        int operationIndex,
        ICollection<ResourceMutationDiagnostic> diagnostics)
    {
        if (!IsSafeTaskId(taskId))
        {
            diagnostics.Add(new("invalid_task_id", operationIndex, [taskId], "Task ID must contain only lowercase letters, digits, and hyphens."));
            return;
        }
        if (!operation.TryGetProperty("if_absent", out var ifAbsent) || ifAbsent.ValueKind != JsonValueKind.True)
        {
            diagnostics.Add(new("precondition_required", operationIndex, [taskId], "if_absent: true is required."));
            return;
        }
        if (staged.ContainsKey(taskId))
        {
            diagnostics.Add(new("conflict", operationIndex, [taskId], "Task ID already exists in the staged graph."));
            return;
        }
        if (!operation.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new("fields_required", operationIndex, [taskId], "fields must be a JSON object."));
            return;
        }

        var task = new GlassworkTask { Id = taskId };
        try
        {
            var error = ApplyFields(task, fields);
            if (error is not null) diagnostics.Add(new("invalid_fields", operationIndex, [taskId], error));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(new("invalid_fields", operationIndex, [taskId], ex.Message));
        }

        if (string.IsNullOrWhiteSpace(task.Title))
            diagnostics.Add(new("title_required", operationIndex, [taskId], "title is required."));
        staged[taskId] = new StagedTask(task, [], true);
    }

    private void ReplaceStagedRelationships(
        IDictionary<string, StagedTask> staged,
        JsonElement operation,
        string taskId,
        int operationIndex,
        ICollection<ResourceMutationDiagnostic> diagnostics)
    {
        if (!staged.TryGetValue(taskId, out var current))
        {
            diagnostics.Add(new("task_not_found", operationIndex, [taskId], "Task was not found."));
            return;
        }

        var name = operation.TryGetProperty("relationship", out var relationship)
            ? relationship.GetString()
            : operation.TryGetProperty("name", out var named) ? named.GetString() : null;
        var values = operation.TryGetProperty("targets", out var targets)
            ? targets
            : operation.TryGetProperty("values", out var valuesElement) ? valuesElement : default;
        if (string.IsNullOrWhiteSpace(name) || values.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new("invalid_relationship_set", operationIndex, [taskId],
                "relationship and targets must name an array-valued relationship set."));
            return;
        }

        try
        {
            var ids = ReadStringArray(values, "targets");
            switch (name.Trim().ToLowerInvariant())
            {
                case "blocked_by":
                case "dependencies":
                    current.Task.BlockedBy = ids;
                    break;
                case "parent":
                    if (ids.Count > 1)
                        diagnostics.Add(new("invalid_relationship_set", operationIndex, [taskId], "parent accepts at most one target."));
                    else current.Task.Parent = ids.SingleOrDefault();
                    break;
                default:
                    diagnostics.Add(new("unsupported_relationship", operationIndex, [taskId],
                        $"Unsupported relationship set '{name}'."));
                    break;
            }
        }
        catch (FormatException ex)
        {
            diagnostics.Add(new("invalid_relationship_set", operationIndex, [taskId], ex.Message));
        }
    }

    private List<ResourceMutationDiagnostic> ValidateStagedGraph(
        IReadOnlyDictionary<string, StagedTask> staged,
        IReadOnlyDictionary<string, int> touchedOperationIndexes)
    {
        var diagnostics = new List<ResourceMutationDiagnostic>();
        var byId = new Dictionary<string, StagedTask>(StringComparer.Ordinal);
        foreach (var pair in staged)
        {
            var canonicalId = pair.Key.Trim().ToLowerInvariant();
            if (!byId.TryAdd(canonicalId, pair.Value))
                diagnostics.Add(new("duplicate_task_id", touchedOperationIndexes.GetValueOrDefault(pair.Key, 0),
                    [pair.Key, canonicalId], "Task IDs collide after canonicalization."));
        }

        foreach (var pair in staged)
        {
            var task = pair.Value.Task;
            var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
            for (var dependencyIndex = 0; dependencyIndex < task.BlockedBy.Count; dependencyIndex++)
            {
                var dependencyId = task.BlockedBy[dependencyIndex].Trim().ToLowerInvariant();
                task.BlockedBy[dependencyIndex] = dependencyId;
                if (!dependencyIds.Add(dependencyId))
                    diagnostics.Add(new("duplicate_relationship_id",
                        touchedOperationIndexes.GetValueOrDefault(task.Id, 0),
                        [task.Id, dependencyId], "Relationship targets must be unique after canonicalization."));
                else if (string.Equals(task.Id, dependencyId, StringComparison.Ordinal))
                    diagnostics.Add(new("self_dependency", touchedOperationIndexes.GetValueOrDefault(task.Id, 0),
                        [task.Id, dependencyId], "A Task cannot depend on itself."));
                else if (!byId.ContainsKey(dependencyId))
                    diagnostics.Add(new("missing_dependency", touchedOperationIndexes.GetValueOrDefault(task.Id, 0),
                        [task.Id, dependencyId], "Dependency target does not exist."));
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in byId.Keys)
            DetectCycle(id, byId, visiting, visited, diagnostics, [], touchedOperationIndexes);
        return diagnostics;
    }

    private static void DetectCycle(
        string id,
        IReadOnlyDictionary<string, StagedTask> byId,
        ISet<string> visiting,
        ISet<string> visited,
        ICollection<ResourceMutationDiagnostic> diagnostics,
        IReadOnlyList<string> path,
        IReadOnlyDictionary<string, int> touchedOperationIndexes)
    {
        if (visited.Contains(id)) return;
        if (!visiting.Add(id))
        {
            diagnostics.Add(new("dependency_cycle", touchedOperationIndexes.GetValueOrDefault(id, 0),
                path.Append(id).ToArray(), "Task dependency graph contains a cycle."));
            return;
        }

        var nextPath = path.Append(id).ToArray();
        foreach (var dependency in byId[id].Task.BlockedBy)
        {
            if (byId.ContainsKey(dependency))
                DetectCycle(dependency, byId, visiting, visited, diagnostics, nextPath, touchedOperationIndexes);
        }

        visiting.Remove(id);
        visited.Add(id);
    }

    private static string? ReadRevision(
        JsonElement operation,
        int operationIndex,
        string? taskId,
        ICollection<ResourceMutationDiagnostic> diagnostics)
    {
        if (!operation.TryGetProperty("if_revision", out var revision)
            && !operation.TryGetProperty("resource_revision", out revision)
            && !operation.TryGetProperty("revision", out revision))
            return null;
        if (revision.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            diagnostics.Add(new("invalid_revision", operationIndex, taskId is null ? [] : [taskId],
                "if_revision must be a string or null."));
            return null;
        }
        return revision.GetString();
    }

    private static bool IsSafeTaskId(string taskId) =>
        RegexSafeTaskId().IsMatch(taskId);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex RegexSafeTaskId();

    private static string HashTransactionRequest(
        string? mutationId,
        string? revision,
        JsonElement operations,
        JsonElement? assertions) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{mutationId ?? string.Empty}\n{revision ?? string.Empty}\n{CanonicalJson(operations)}\n"
            + (assertions is { } value ? CanonicalJson(value) : string.Empty)))).ToLowerInvariant();

    private static ResourceMutationOutcome TransactionError(
        string mutationId,
        string outcome,
        string message,
        ResourceMutationDiagnostic diagnostic) =>
        new(mutationId, outcome, false, null, null, null, message, Diagnostics: [diagnostic]);

    private static ResourceMutationOutcome TransactionError(
        string mutationId,
        string outcome,
        string message,
        IReadOnlyList<ResourceMutationDiagnostic> diagnostics) =>
        new(mutationId, outcome, false, null, null, null, message, Diagnostics: diagnostics);

    private sealed record StagedTask(GlassworkTask Task, byte[] OriginalBytes, bool Created);

    public ResourceMutationOutcome CreateTask(
        string? mutationId,
        string? taskId,
        bool? ifAbsent,
        JsonElement fields)
    {
        var notifications = new HashSet<string>(StringComparer.Ordinal);
        ResourceMutationOutcome? result = null;

        try
        {
            using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
            {
                notifications.UnionWith(RecoverUnsafe());
                result = CreateTaskUnsafe(mutationId, taskId, ifAbsent, fields, notifications);
            }
        }
        catch
        {
            try
            {
                using (VaultScopedCoordinator.EnterExclusive(_vaultPath))
                    notifications.UnionWith(RecoverUnsafe());
            }
            catch
            {
                // Preserve the original failure; the next managed access retries recovery.
            }

            throw;
        }
        finally
        {
            foreach (var taskIdToNotify in notifications)
                _vault.NotifyTaskWritten(taskIdToNotify);
        }

        return result!;
    }

    private ResourceMutationOutcome CreateTaskUnsafe(
        string? mutationId,
        string? taskId,
        bool? ifAbsent,
        JsonElement fields,
        ISet<string> notifications)
    {
        if (string.IsNullOrWhiteSpace(mutationId) || ifAbsent != true)
            return new ResourceMutationOutcome(mutationId ?? string.Empty, "precondition_required", false, null, null, null, "mutation_id and if_absent: true are required.");

        if (string.IsNullOrWhiteSpace(taskId))
            return new ResourceMutationOutcome(mutationId, "validation_error", false, null, null, null, "task_id is required.");
        taskId = taskId.Trim();

        RecoverUnsafe();
        var state = ReadState();
        Prune(state);
        var requestHash = HashRequest(mutationId, "create_task", taskId, ifAbsent?.ToString(), fields);
        if (state.Outcomes.TryGetValue(mutationId, out var recorded))
        {
            if (recorded.RequestHash != requestHash)
                return new ResourceMutationOutcome(mutationId, "mutation_id_reused", false, null, null, null, "mutation_id was already used for a different request.");
            return recorded.Outcome with { Replayed = true };
        }

        var existingBytes = _vault.TryReadBytesUnsafe(taskId);
        if (existingBytes is not null)
        {
            var currentRevision = Revision(existingBytes);
            var currentTask = _parser.Parse(Encoding.UTF8.GetString(existingBytes));
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "conflict", false, null, currentRevision,
                    Snapshot(currentTask, currentRevision), "Task ID already exists."));
        }

        if (fields.ValueKind != JsonValueKind.Object)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(mutationId, "validation_error", false, null, null, null, "fields must be a JSON object."));

        var task = new GlassworkTask { Id = taskId };
        var error = ApplyFields(task, fields);
        if (error is not null)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(mutationId, "validation_error", false, null, null, null, error));
        if (string.IsNullOrWhiteSpace(task.Title))
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(mutationId, "validation_error", false, null, null, null, "title is required."));
        if (!IsValidPriority(task.Priority))
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(mutationId, "validation_error", false, null, null, null, "priority is invalid."));

        var createdBytes = Encoding.UTF8.GetBytes(_parser.Serialize(task));
        var journal = new JournalEntry(
            taskId,
            Original: null,
            Convert.ToBase64String(createdBytes),
            mutationId,
            requestHash,
            ExpectedRevision: null,
            Committed: false,
            Existed: false);
        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeJournal);
        WriteJournal(journal);
        try
        {
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
            _vault.ReplaceBytesUnsafe(taskId, createdBytes);
            notifications.Add(taskId);
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterReplacementBeforeCommit);
            WriteJournal(journal with { Committed = true });
            var revision = Revision(createdBytes);
            var applied = new ResourceMutationOutcome(
                mutationId, "applied", false, null, revision,
                Snapshot(task, revision));
            var recordedOutcome = Record(state, mutationId, requestHash, applied);
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.AfterCommit);
            DeleteJournal();
            return recordedOutcome;
        }
        catch
        {
            RecoverUnsafe();
            throw;
        }
    }

    private ResourceMutationOutcome Record(State state, string id, string hash, ResourceMutationOutcome outcome)
    {
        state.Outcomes[id] = new RecordedOutcome(hash, _clock(), outcome);
        WriteState(state);
        return outcome;
    }

    private string? ApplyFields(GlassworkTask task, JsonElement fields)
    {
        var repairingMalformedBlocker = task.IsBlocked
            && task.NeedsBlockerDetails
            && fields.TryGetProperty("blocked_reason", out _)
            && fields.TryGetProperty("blocked_from_status", out _);
        if (!repairingMalformedBlocker)
            TaskService.EnsureCanMutate(task);
        string? requestedStatus = null;
        var hasBlockedReason = false;
        var hasBlockedFromStatus = false;

        foreach (var property in fields.EnumerateObject())
        {
            switch (property.Name)
            {
                case "title": task.Title = ReadString(property.Value, property.Name) ?? string.Empty; break;
                case "status":
                    requestedStatus = NormalizeStatus(ReadString(property.Value, property.Name));
                    break;
                case "blocked_reason":
                    task.BlockedReason = ReadString(property.Value, property.Name);
                    hasBlockedReason = true;
                    break;
                case "blocked_from_status":
                    task.BlockedFromStatus = NormalizeBlockedFromStatus(
                        ReadString(property.Value, property.Name));
                    hasBlockedFromStatus = true;
                    break;
                case "priority": task.Priority = ReadString(property.Value, property.Name) ?? string.Empty; break;
                case "type": task.Type = GlassworkTask.Types.Normalize(ReadString(property.Value, property.Name)); break;
                case "size":
                {
                    var requestedSize = ReadString(property.Value, property.Name);
                    if (!CanApplySize(task.Size, requestedSize))
                        return "size must be quick, short, focus, deep, break_down, or null.";
                    task.Size = requestedSize;
                    break;
                }
                case "parent_task_id": task.Parent = ReadString(property.Value, property.Name); break;
                case "tags": task.Tags = ReadStringArray(property.Value, property.Name); break;
                case "blocked_by": task.BlockedBy = ReadStringArray(property.Value, property.Name); break;
                case "context_links": task.ContextLinks = ReadStringArray(property.Value, property.Name); break;
                case "ado_link": task.AdoLink = ReadNullableInt(property.Value, property.Name); break;
                case "ado_title": task.AdoTitle = ReadString(property.Value, property.Name); break;
                case "links": task.Links = ReadLinks(property.Value, property.Name); break;
                case "subtasks":
                {
                    var requestedSubtasks = ReadSubtasks(property.Value, property.Name);
                    for (var index = 0; index < requestedSubtasks.Count; index++)
                    {
                        var requestedSize = requestedSubtasks[index].Size;
                        if (!CanApplySubtaskSize(task.Subtasks, requestedSize))
                            return $"subtasks[{index}].size must be quick, short, focus, deep, break_down, or null.";
                    }
                    task.Subtasks = requestedSubtasks;
                    break;
                }
                case "description": task.Description = ReadString(property.Value, property.Name) ?? string.Empty; break;
                case "notes":
                    if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("append", out var append))
                    {
                        var addition = ReadString(append, "notes.append") ?? string.Empty;
                        task.Notes = string.IsNullOrEmpty(task.Notes) ? addition : $"{task.Notes}\n{addition}";
                    }
                    else task.Notes = ReadString(property.Value, property.Name) ?? string.Empty;
                    break;
                case "due_date": task.Due = ReadDate(property.Value, property.Name); break;
                case "scheduled": task.MyDay = ReadDate(property.Value, property.Name); break;
                case "created": task.Created = ReadDate(property.Value, property.Name) ?? task.Created; break;
                case "start": task.Start = ReadDate(property.Value, property.Name); break;
                case "defer_until": task.DeferUntil = ReadDate(property.Value, property.Name); break;
                case "completed_at": task.CompletedAt = ReadDate(property.Value, property.Name); break;
                default: return $"Unsupported task field '{property.Name}'.";
            }
        }

        if (requestedStatus is not null)
        {
            if (requestedStatus == GlassworkTask.Statuses.Blocked)
            {
                if (task.Status != GlassworkTask.Statuses.Blocked)
                {
                    if (string.IsNullOrWhiteSpace(task.BlockedReason))
                        return "blocked_reason is required when status is blocked.";
                    TaskService.ApplyMarkBlocked(task, task.BlockedReason, _clock);
                }
                else if (hasBlockedReason)
                {
                    if (task.NeedsBlockerDetails && hasBlockedFromStatus)
                        TaskService.ApplyRepairBlocked(task, task.BlockedReason ?? string.Empty,
                            task.BlockedFromStatus!, _clock);
                    else
                        TaskService.ApplyEditBlockedReason(task, task.BlockedReason ?? string.Empty);
                }
            }
            else
            {
                TaskService.ApplySetStatus(task, requestedStatus, () => _clock().LocalDateTime);
            }
        }
        else if (hasBlockedReason)
        {
            if (!task.IsBlocked)
                return "blocked_reason can only be set on a blocked Task.";
            TaskService.ApplyEditBlockedReason(task, task.BlockedReason ?? string.Empty);
        }

        if (hasBlockedFromStatus)
        {
            if (!task.IsBlocked)
                return "blocked_from_status can only be set on a blocked Task.";
            if (task.BlockedReason is null)
                return "blocked_reason is required when status is blocked.";
        }

        return null;
    }

    private static string NormalizeStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "doing" => GlassworkTask.Statuses.InProgress,
            GlassworkTask.Statuses.Todo => GlassworkTask.Statuses.Todo,
            GlassworkTask.Statuses.InProgress => GlassworkTask.Statuses.InProgress,
            GlassworkTask.Statuses.Done => GlassworkTask.Statuses.Done,
            GlassworkTask.Statuses.Blocked => GlassworkTask.Statuses.Blocked,
            _ => throw new FormatException("status is invalid.")
        };

    private static string NormalizeBlockedFromStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "todo" => GlassworkTask.Statuses.Todo,
            "doing" => GlassworkTask.Statuses.InProgress,
            "in-progress" => GlassworkTask.Statuses.InProgress,
            _ => throw new FormatException("blocked_from_status must be todo or doing.")
        };

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"{name} must be a string or null.");

    private static int? ReadNullableInt(JsonElement value, string name)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        throw new FormatException($"{name} must be an integer or null.");
    }

    private static DateTime? ReadDate(JsonElement value, string name)
    {
        var raw = ReadString(value, name);
        if (raw is null) return null;
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)) return date;
        throw new FormatException($"{name} must use yyyy-MM-dd.");
    }

    private static List<string> ReadStringArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{name} must be an array of strings.");

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new FormatException($"{name} must be an array of strings.");
            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text.Trim());
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<TaskLink> ReadLinks(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{name} must be an array.");

        var result = new List<TaskLink>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out var type)
                || !item.TryGetProperty("value", out var linkValue)
                || type.ValueKind != JsonValueKind.String
                || linkValue.ValueKind != JsonValueKind.String)
                throw new FormatException($"{name} must contain link objects with type and value.");

            result.Add(new TaskLink
            {
                Type = type.GetString()!,
                Value = linkValue.GetString()!,
                Label = item.TryGetProperty("label", out var label) ? ReadString(label, $"{name}.label") : null
            });
        }
        return result;
    }

    private static List<SubTask> ReadSubtasks(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{name} must be an array.");

        var result = new List<SubTask>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
                throw new FormatException($"{name} must contain objects with text.");

            var subtask = new SubTask
            {
                Text = text.GetString()!,
                IsCompleted = item.TryGetProperty("is_completed", out var completed)
                    && completed.ValueKind == JsonValueKind.True,
                Status = item.TryGetProperty("status", out var status) ? ReadString(status, $"{name}.status") : null,
                Notes = item.TryGetProperty("notes", out var notes) ? ReadString(notes, $"{name}.notes") ?? string.Empty : string.Empty
            };
            if (item.TryGetProperty("size", out var size))
                subtask.Size = ReadString(size, $"{name}.size");

            if (item.TryGetProperty("metadata", out var metadata))
            {
                if (metadata.ValueKind != JsonValueKind.Object)
                    throw new FormatException($"{name}.metadata must be an object.");
                foreach (var entry in metadata.EnumerateObject())
                    subtask.Metadata[entry.Name] = ReadString(entry.Value, $"{name}.metadata.{entry.Name}") ?? string.Empty;
            }
            subtask.Size = subtask.Size;
            result.Add(subtask);
        }
        return result;
    }

    private static bool IsValidPriority(string priority) =>
        priority is GlassworkTask.Priorities.Low
            or GlassworkTask.Priorities.Medium
            or GlassworkTask.Priorities.High
            or GlassworkTask.Priorities.Urgent;

    private static bool CanApplySize(string? existingSize, string? requestedSize) =>
        string.IsNullOrWhiteSpace(requestedSize)
        || SizeBuckets.TryParse(requestedSize, out _)
        || string.Equals(existingSize, requestedSize, StringComparison.Ordinal);

    private static bool CanApplySubtaskSize(
        IReadOnlyList<SubTask> existingSubtasks,
        string? requestedSize) =>
        string.IsNullOrWhiteSpace(requestedSize)
        || SizeBuckets.TryParse(requestedSize, out _)
        || existingSubtasks.Any(existing =>
            string.Equals(existing.Size, requestedSize, StringComparison.Ordinal));

    private ResourceMutationTaskSnapshot Snapshot(GlassworkTask task, string revision) =>
        new(task.Id, task.Title, task.Status == GlassworkTask.Statuses.InProgress ? "doing" : task.Status,
            task.Priority, task.Type, task.Size, task.Created, task.Due, task.Start, task.MyDay, task.DeferUntil,
            task.Parent, task.Description, task.Notes, task.Tags, task.BlockedBy, task.CompletedAt,
            task.CancelledAt, task.CancellationReason, task.BlockedReason, revision,
            task.Subtasks.Select(subtask => new ResourceMutationSubtaskSnapshot(
                subtask.Text,
                subtask.IsCompleted,
                subtask.Status,
                subtask.Size,
                new Dictionary<string, string>(subtask.Metadata, StringComparer.Ordinal),
                subtask.Notes)).ToArray());

    public static string Revision(byte[] bytes) =>
        $"rr1-{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private bool SemanticallyEqual(GlassworkTask left, GlassworkTask right) =>
        string.Equals(_parser.Serialize(left), _parser.Serialize(right), StringComparison.Ordinal);

    private static string HashRequest(
        string? id,
        string operation,
        string? taskId,
        string? revision,
        JsonElement fields) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        $"{id ?? string.Empty}\n{operation}\n{taskId ?? string.Empty}\n{revision ?? string.Empty}\n{CanonicalJson(fields)}")))
            .ToLowerInvariant();

    private static string CanonicalJson(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => "{"
                + string.Join(
                    ",",
                    value.EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                        .Select(property =>
                            JsonSerializer.Serialize(property.Name)
                            + ":"
                            + CanonicalJson(property.Value)))
                + "}",
            JsonValueKind.Array => "["
                + string.Join(",", value.EnumerateArray().Select(CanonicalJson))
                + "]",
            JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private sealed class State
    {
        public Dictionary<string, RecordedOutcome> Outcomes { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record RecordedOutcome(string RequestHash, DateTimeOffset CreatedAt, ResourceMutationOutcome Outcome);
    private sealed record JournalEntry(
        string TaskId,
        string? Original,
        string Updated,
        string MutationId,
        string RequestHash,
        string? ExpectedRevision,
        bool Committed,
        bool Existed = true,
        bool Deleted = false,
        string? OwnedPath = null);

    private State ReadState()
    {
        if (!File.Exists(_statePath)) return new State();
        return JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath)) ?? new State();
    }

    private void WriteState(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temp = _statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        if (File.Exists(_statePath)) File.Replace(temp, _statePath, null);
        else File.Move(temp, _statePath);
    }

    private void Prune(State state)
    {
        var cutoff = _clock().AddDays(-RetentionDays);
        foreach (var key in state.Outcomes.Where(x => x.Value.CreatedAt <= cutoff).Select(x => x.Key).ToList())
            state.Outcomes.Remove(key);
    }

    private string JournalPath => Path.Combine(_vaultPath, ".glasswork", "mutation-journal.json");

    private void WriteJournal(JournalEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var temp = JournalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry));
        if (File.Exists(JournalPath)) File.Replace(temp, JournalPath, null);
        else File.Move(temp, JournalPath);
    }

    private void DeleteJournal()
    {
        if (File.Exists(JournalPath)) File.Delete(JournalPath);
    }

    private IReadOnlyList<string> RecoverWithExclusiveLease()
    {
        using var lease = VaultScopedCoordinator.EnterExclusive(_vaultPath);
        return RecoverUnsafe();
    }

    private IReadOnlyList<string> DrainRecoveredDeletes()
    {
        lock (_recoveredDeletesGate)
        {
            var deleted = _recoveredDeletes.ToArray();
            _recoveredDeletes.Clear();
            return deleted;
        }
    }

    private void NotifyRecoveredDeletes(ISet<string>? alreadyNotified = null)
    {
        foreach (var taskId in DrainRecoveredDeletes())
        {
            if (alreadyNotified?.Contains(taskId) != true)
                _vault.NotifyTaskDeleted(taskId);
        }
    }

    private IReadOnlyList<string> RecoverUnsafe()
    {
        if (!File.Exists(JournalPath))
        {
            CleanupOrphanDeletionOperations();
            return Array.Empty<string>();
        }

        var isTaskDeletionJournal = false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(JournalPath));
            if (document.RootElement.TryGetProperty("Kind", out var kind)
                && string.Equals(kind.GetString(), TaskDeletionJournalKind, StringComparison.Ordinal))
            {
                isTaskDeletionJournal = true;
                return RecoverTaskDeletionUnsafe(document.RootElement);
            }
            if (document.RootElement.TryGetProperty("Entries", out _)
                || document.RootElement.TryGetProperty("entries", out _))
                return RecoverGraphUnsafe(document.RootElement);

            var entry = JsonSerializer.Deserialize<JournalEntry>(document.RootElement.GetRawText())
                ?? throw new InvalidDataException("Mutation journal is invalid.");
            if (entry.OwnedPath is not null)
            {
                var bytes = entry.Committed
                    ? Convert.FromBase64String(entry.Updated)
                    : entry.Original is null ? null : Convert.FromBase64String(entry.Original);
                if (bytes is null)
                {
                    _vault.DeleteOwnedFileUnsafe(entry.OwnedPath);
                }
                else
                {
                    _vault.ReplaceOwnedFileUnsafe(entry.OwnedPath, bytes, overwrite: true);
                }
                DeleteJournal();
                return Array.Empty<string>();
            }
            if (entry.Deleted && entry.Committed)
            {
                lock (_recoveredDeletesGate)
                    _recoveredDeletes.Add(entry.TaskId);
            }
            if (!entry.Existed && !entry.Committed)
            {
                if (File.Exists(Path.Combine(_vaultPath, $"{entry.TaskId}.md")))
                    _vault.DeleteUnsafe(entry.TaskId);
            }

            else if (entry.Deleted && entry.Committed)
            {
                if (File.Exists(Path.Combine(_vaultPath, $"{entry.TaskId}.md")))
                    _vault.DeleteUnsafe(entry.TaskId);
                _vault.ForgetManagedBytes(entry.TaskId);
            }
            else
            {
                var bytes = Convert.FromBase64String(entry.Committed ? entry.Updated : entry.Original!);
                _vault.ReplaceBytesUnsafe(entry.TaskId, bytes);
            }

            if (entry.Committed && !entry.Deleted && !string.IsNullOrWhiteSpace(entry.MutationId))
            {
                var bytes = Convert.FromBase64String(entry.Updated);
                var task = _parser.Parse(Encoding.UTF8.GetString(bytes));
                var revision = Revision(bytes);
                var state = ReadState();
                Prune(state);
                if (!state.Outcomes.ContainsKey(entry.MutationId))
                {
                    state.Outcomes[entry.MutationId] = new RecordedOutcome(
                        entry.RequestHash,
                        _clock(),
                        new ResourceMutationOutcome(
                            entry.MutationId,
                            "applied",
                            false,
                            entry.ExpectedRevision,
                            revision,
                            Snapshot(task, revision)));
                    WriteState(state);
                }
            }

            DeleteJournal();
            return entry.Committed && !entry.Deleted ? [entry.TaskId] : Array.Empty<string>();
        }
        catch (JsonException ex) when (HasPendingDeletionOperations())
        {
            throw new InvalidDataException(
                "Task deletion recovery is blocked because its journal could not be parsed safely.",
                ex);
        }
        catch (FormatException ex) when (isTaskDeletionJournal || HasPendingDeletionOperations())
        {
            throw new InvalidDataException(
                "Task deletion recovery is blocked because its journal content is invalid.",
                ex);
        }
        catch (InvalidDataException ex) when (isTaskDeletionJournal || HasPendingDeletionOperations())
        {
            throw new InvalidDataException(
                "Task deletion recovery could not safely validate its journal.",
                ex);
        }
        catch (JsonException)
        {
            ArchiveInvalidJournal();
            return Array.Empty<string>();
        }
        catch (FormatException)
        {
            ArchiveInvalidJournal();
            return Array.Empty<string>();
        }
        catch (InvalidDataException)
        {
            ArchiveInvalidJournal();
            return Array.Empty<string>();
        }
    }

    private IReadOnlyList<string> RecoverGraphUnsafe(JsonElement root)
    {
        var entry = JsonSerializer.Deserialize<GraphJournalEntry>(root.GetRawText())
            ?? throw new InvalidDataException("Graph mutation journal is invalid.");
        var recovered = new List<string>();
        foreach (var task in entry.Entries)
        {
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringRecovery);
            if (entry.Committed)
                _vault.ReplaceBytesUnsafe(task.TaskId, Convert.FromBase64String(task.Updated));
            else if (task.Created)
                _vault.DeleteUnsafe(task.TaskId);
            else if (task.Original is not null)
                _vault.ReplaceBytesUnsafe(task.TaskId, Convert.FromBase64String(task.Original));
            if (entry.Committed)
                recovered.Add(task.TaskId);
        }

        if (entry.Committed && !string.IsNullOrWhiteSpace(entry.MutationId))
        {
            var state = ReadState();
            Prune(state);
            if (!state.Outcomes.ContainsKey(entry.MutationId))
            {
                var snapshots = entry.Entries
                    .OrderBy(item => item.TaskId, StringComparer.Ordinal)
                    .Select(item =>
                    {
                        var bytes = Convert.FromBase64String(item.Updated);
                        var task = _parser.Parse(Encoding.UTF8.GetString(bytes));
                        return Snapshot(task, Revision(bytes));
                    })
                    .ToArray();
                state.Outcomes[entry.MutationId] = new RecordedOutcome(
                    entry.RequestHash,
                    _clock(),
                    new ResourceMutationOutcome(
                        entry.MutationId,
                        "applied",
                        false,
                        entry.ExpectedRevision,
                        null,
                        snapshots.FirstOrDefault(),
                        Tasks: snapshots));
                WriteState(state);
            }
        }

        DeleteJournal();
        return recovered;
    }

    private void ArchiveInvalidJournal()
    {
        if (!File.Exists(JournalPath)) return;
        var archivePath = JournalPath + $".corrupt-{Guid.NewGuid():N}";
        try
        {
            File.Move(JournalPath, archivePath);
        }
        catch
        {
            try { File.Delete(JournalPath); }
            catch { /* Best effort: the next managed access will retry. */ }
        }

    }

    private void WriteGraphJournal(GraphJournalEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        var temp = JournalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry));
        if (File.Exists(JournalPath)) File.Replace(temp, JournalPath, null);
        else File.Move(temp, JournalPath);
    }

    private sealed record GraphJournalEntry(
        string MutationId,
        string RequestHash,
        string? ExpectedRevision,
        bool Committed,
        IReadOnlyList<GraphJournalTaskEntry> Entries);

    private sealed record GraphJournalTaskEntry(
        string TaskId,
        string? Original,
        string Updated,
        bool Created);
}
