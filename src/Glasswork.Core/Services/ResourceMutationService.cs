using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public enum ResourceMutationFailurePoint
{
    BeforeJournal,
    BeforeFinalValidation,
    DuringReplacement,
    AfterReplacementBeforeCommit,
    AfterCommit
}

public interface IResourceMutationFaultInjector
{
    void ThrowIfInjected(ResourceMutationFailurePoint point);
}

public sealed record ResourceMutationTaskSnapshot(
    string Id,
    string Title,
    string Status,
    string Priority,
    string Type,
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
    string? BlockedReason,
    string ResourceRevision);

public sealed record ResourceMutationOutcome(
    string MutationId,
    string Outcome,
    bool Replayed,
    string? ExpectedRevision,
    string? CurrentRevision,
    ResourceMutationTaskSnapshot? Task,
    string? Error = null);

/// <summary>
/// Durable, conditional single-resource mutation boundary.
/// </summary>
public sealed class ResourceMutationService
{
    private const int RetentionDays = 30;
    private readonly string _vaultPath;
    private readonly VaultService _vault;
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IResourceMutationFaultInjector? _faults;
    private readonly FrontmatterParser _parser = new();

    public ResourceMutationService(
        string vaultPath,
        VaultService? vault = null,
        Func<DateTimeOffset>? clock = null,
        IResourceMutationFaultInjector? faults = null)
    {
        _vaultPath = vaultPath;
        _vault = vault ?? new VaultService(vaultPath);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _faults = faults;
        _statePath = Path.Combine(vaultPath, ".glasswork", "resource-mutations.json");
        _vault.RegisterManagedRecovery(RecoverWithExclusiveLease);
        _vault.RunManagedRecovery();
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
                result = TransactSingleTaskUnsafe(mutationId, taskId, ifRevision, fields, notifications);
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
        JsonElement fields,
        ISet<string> notifications)
    {
        var state = ReadState();
        Prune(state);
        var requestHash = HashRequest(mutationId, "set_task_fields", taskId, ifRevision, fields);

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

        if (fields.ValueKind != JsonValueKind.Object)
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
            error = ApplyFields(staged, fields);
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
                case "parent_task_id": task.Parent = ReadString(property.Value, property.Name); break;
                case "tags": task.Tags = ReadStringArray(property.Value, property.Name); break;
                case "blocked_by": task.BlockedBy = ReadStringArray(property.Value, property.Name); break;
                case "context_links": task.ContextLinks = ReadStringArray(property.Value, property.Name); break;
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

    private static bool IsValidPriority(string priority) =>
        priority is GlassworkTask.Priorities.Low
            or GlassworkTask.Priorities.Medium
            or GlassworkTask.Priorities.High
            or GlassworkTask.Priorities.Urgent;

    private ResourceMutationTaskSnapshot Snapshot(GlassworkTask task, string revision) =>
        new(task.Id, task.Title, task.Status == GlassworkTask.Statuses.InProgress ? "doing" : task.Status,
            task.Priority, task.Type, task.Created, task.Due, task.Start, task.MyDay, task.DeferUntil,
            task.Parent, task.Description, task.Notes, task.Tags, task.BlockedBy, task.CompletedAt,
            task.BlockedReason, revision);

    private static string Revision(byte[] bytes) =>
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
        bool Existed = true);

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

    private IReadOnlyList<string> RecoverUnsafe()
    {
        if (!File.Exists(JournalPath)) return Array.Empty<string>();

        try
        {
            var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(JournalPath))
                ?? throw new InvalidDataException("Mutation journal is invalid.");
            if (!entry.Existed && !entry.Committed)
            {
                if (File.Exists(Path.Combine(_vaultPath, $"{entry.TaskId}.md")))
                    _vault.DeleteUnsafe(entry.TaskId);
            }
            else
            {
                var bytes = Convert.FromBase64String(entry.Committed ? entry.Updated : entry.Original!);
                _vault.ReplaceBytesUnsafe(entry.TaskId, bytes);
            }

            if (entry.Committed && !string.IsNullOrWhiteSpace(entry.MutationId))
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
            return [entry.TaskId];
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
}
