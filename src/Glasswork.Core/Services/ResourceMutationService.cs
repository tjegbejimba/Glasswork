using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public enum ResourceMutationFailurePoint
{
    BeforeJournal,
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
    private readonly object _stateGate = new();

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
        Recover();
    }

    public ResourceMutationOutcome TransactSingleTask(
        string? mutationId,
        string? taskId,
        string? ifRevision,
        JsonElement fields)
    {
        if (string.IsNullOrWhiteSpace(mutationId) || string.IsNullOrWhiteSpace(ifRevision))
            return new ResourceMutationOutcome(mutationId ?? string.Empty, "precondition_required", false, ifRevision, null, null, "mutation_id and if_revision are required.");

        if (string.IsNullOrWhiteSpace(taskId))
            return new ResourceMutationOutcome(mutationId, "validation_error", false, ifRevision, null, null, "task_id is required.");

        using var lease = VaultScopedCoordinator.EnterExclusive(_vaultPath);
        Recover();
        var state = ReadState();
        Prune(state);
        var requestHash = HashRequest(mutationId, taskId, ifRevision, fields);
        if (state.Outcomes.TryGetValue(mutationId, out var recorded))
        {
            if (recorded.RequestHash != requestHash)
                return new ResourceMutationOutcome(mutationId, "mutation_id_reused", false, ifRevision, null, null, "mutation_id was already used for a different request.");
            return recorded.Outcome with { Replayed = true };
        }

        var bytes = _vault.TryReadBytesUnsafe(taskId);
        if (bytes is null)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(mutationId, "not_found", false, ifRevision, null, null, "Task was not found."));

        var currentRevision = Revision(bytes);
        var currentTask = _parser.Parse(Encoding.UTF8.GetString(bytes));
        if (!string.Equals(ifRevision, currentRevision, StringComparison.Ordinal))
        {
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(
                    mutationId, "conflict", false, ifRevision, currentRevision,
                    Snapshot(currentTask, currentRevision), "if_revision does not match the current Resource Revision."));
        }

        if (fields.ValueKind != JsonValueKind.Object)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(mutationId, "validation_error", false, ifRevision, currentRevision, null, "fields must be a JSON object."));

        var staged = _parser.Parse(Encoding.UTF8.GetString(bytes));
        var error = ApplyFields(staged, fields);
        if (error is not null)
            return Record(state, mutationId, requestHash,
                new ResourceMutationOutcome(mutationId, "validation_error", false, ifRevision, currentRevision, null, error));

        var updatedBytes = Encoding.UTF8.GetBytes(_parser.Serialize(staged));
        if (bytes.AsSpan().SequenceEqual(updatedBytes))
        {
            var noOp = new ResourceMutationOutcome(
                mutationId, "no_op", false, ifRevision, currentRevision,
                Snapshot(currentTask, currentRevision));
            return Record(state, mutationId, requestHash, noOp);
        }

        var journal = new JournalEntry(
            taskId,
            Convert.ToBase64String(bytes),
            Convert.ToBase64String(updatedBytes),
            Committed: false);
        _faults?.ThrowIfInjected(ResourceMutationFailurePoint.BeforeJournal);
        WriteJournal(journal);
        try
        {
            _faults?.ThrowIfInjected(ResourceMutationFailurePoint.DuringReplacement);
            _vault.ReplaceBytesUnsafe(taskId, updatedBytes);
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
        catch
        {
            Recover();
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
        var originalStatus = task.Status;
        foreach (var property in fields.EnumerateObject())
        {
            switch (property.Name)
            {
                case "title": task.Title = ReadString(property.Value, property.Name) ?? string.Empty; break;
                case "status":
                    var status = ReadString(property.Value, property.Name);
                    task.Status = status switch { "doing" => GlassworkTask.Statuses.InProgress, "todo" or "in-progress" or "blocked" or "done" => status, _ => string.Empty };
                    if (task.Status.Length == 0) return "status is invalid.";
                    break;
                case "blocked_reason": task.BlockedReason = ReadString(property.Value, property.Name); break;
                case "blocked_from_status":
                    var blockedFromStatus = ReadString(property.Value, property.Name);
                    task.BlockedFromStatus = blockedFromStatus switch
                    {
                        "doing" => GlassworkTask.Statuses.InProgress,
                        "todo" or "in-progress" => blockedFromStatus == "todo" ? GlassworkTask.Statuses.Todo : GlassworkTask.Statuses.InProgress,
                        _ => null
                    };
                    if (task.BlockedFromStatus is null) return "blocked_from_status must be todo or doing.";
                    break;
                case "priority": task.Priority = ReadString(property.Value, property.Name) ?? string.Empty; break;
                case "type": task.Type = GlassworkTask.Types.Normalize(ReadString(property.Value, property.Name)); break;
                case "parent_task_id": task.Parent = ReadString(property.Value, property.Name); break;
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
                default: return $"Unsupported task field '{property.Name}'.";
            }
        }
        if (task.Status == GlassworkTask.Statuses.Blocked)
        {
            if (string.IsNullOrWhiteSpace(task.BlockedReason)) return "blocked_reason is required when status is blocked.";
            task.BlockedAt ??= _clock();
            task.BlockedFromStatus ??= originalStatus is GlassworkTask.Statuses.Todo or GlassworkTask.Statuses.InProgress
                ? originalStatus
                : GlassworkTask.Statuses.Todo;
        }
        else
        {
            task.BlockedReason = null;
            task.BlockedAt = null;
            task.BlockedFromStatus = null;
        }
        return null;
    }

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

    private ResourceMutationTaskSnapshot Snapshot(GlassworkTask task, string revision) =>
        new(task.Id, task.Title, task.Status == GlassworkTask.Statuses.InProgress ? "doing" : task.Status,
            task.Priority, task.Type, task.Created, task.Due, task.Start, task.MyDay, task.DeferUntil,
            task.Parent, task.Description, task.Notes, task.Tags, revision);

    private static string Revision(byte[] bytes) =>
        $"rr1-{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private static string HashRequest(string id, string taskId, string revision, JsonElement fields) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\n{taskId}\n{revision}\n{fields.GetRawText()}"))).ToLowerInvariant();

    private sealed class State
    {
        public Dictionary<string, RecordedOutcome> Outcomes { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed record RecordedOutcome(string RequestHash, DateTimeOffset CreatedAt, ResourceMutationOutcome Outcome);
    private sealed record JournalEntry(string TaskId, string Original, string Updated, bool Committed);

    private State ReadState()
    {
        lock (_stateGate)
        {
            if (!File.Exists(_statePath)) return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath)) ?? new State();
        }
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
        File.WriteAllText(JournalPath, JsonSerializer.Serialize(entry));
    }

    private void DeleteJournal()
    {
        if (File.Exists(JournalPath)) File.Delete(JournalPath);
    }

    private void Recover()
    {
        if (!File.Exists(JournalPath)) return;
        var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(JournalPath))
            ?? throw new InvalidDataException("Mutation journal is invalid.");
        var bytes = Convert.FromBase64String(entry.Committed ? entry.Updated : entry.Original);
        _vault.ReplaceBytesUnsafe(entry.TaskId, bytes);
        DeleteJournal();
    }
}
