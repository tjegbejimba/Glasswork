using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

internal static partial class ParentMigrationCommand
{
    public static int Run(string[] args, JsonSerializerOptions json)
    {
        if (args.Length == 0)
            return Usage("parent-migration requires a phase.");

        try
        {
            return args[0] switch
            {
                "dry-run" => DryRun(args[1..], json),
                "execute" => Execute(args[1..], json),
                "validate" => Validate(args[1..], json),
                "rollback" => Rollback(args[1..], json),
                _ => Usage($"Unknown parent-migration phase: '{args[0]}'."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int DryRun(string[] args, JsonSerializerOptions json)
    {
        var vaultRoot = RequireOption(args, "--vault");
        var evidencePath = RequireOption(args, "--ado-evidence");
        var planPath = RequireOption(args, "--plan");
        if (vaultRoot is null || evidencePath is null || planPath is null)
            return 2;

        var planner = new ParentMigrationPlanner(vaultRoot, evidencePath);
        var plan = planner.CreatePlan();
        if (ParentMigrationExecutor.IsPathUnder(planPath, vaultRoot))
        {
            Console.Error.WriteLine("error: migration plan must be written outside the Vault.");
            return 1;
        }
        var planJson = JsonSerializer.Serialize(plan, json);
        WriteAtomic(planPath, Encoding.UTF8.GetBytes(planJson));

        var report = new ParentMigrationReport(
            plan.BlockingDiagnostics.Count == 0 ? "ready" : "blocked",
            plan.Changes.Count(change => change.Kind == "update" && change.LegacyParent),
            plan.Promotions.Count,
            plan.UnresolvedSourceKinds.Count,
            plan.BlockingDiagnostics,
            plan.OperationId,
            plan.PlanHash);
        Console.WriteLine(JsonSerializer.Serialize(report, json));
        return plan.BlockingDiagnostics.Count == 0 ? 0 : 1;
    }

    private static int Execute(string[] args, JsonSerializerOptions json)
    {
        var vaultRoot = RequireOption(args, "--vault");
        var planPath = RequireOption(args, "--plan");
        var backupPath = RequireOption(args, "--backup");
        if (vaultRoot is null || planPath is null || backupPath is null)
            return 2;

        var executor = new ParentMigrationExecutor(vaultRoot, planPath, backupPath, json);
        var result = executor.Execute(fixtureMode: Array.IndexOf(args, "--fixture") >= 0);
        Console.WriteLine(JsonSerializer.Serialize(result, json));
        return result.Outcome == "applied" ? 0 : 1;
    }

    private static int Validate(string[] args, JsonSerializerOptions json)
    {
        var vaultRoot = RequireOption(args, "--vault");
        var planPath = RequireOption(args, "--plan");
        var backupPath = RequireOption(args, "--backup");
        if (vaultRoot is null || planPath is null || backupPath is null)
            return 2;

        var executor = new ParentMigrationExecutor(vaultRoot, planPath, backupPath, json);
        var result = executor.ValidateApplied();
        Console.WriteLine(JsonSerializer.Serialize(result, json));
        return result.Outcome == "valid" ? 0 : 1;
    }

    private static int Rollback(string[] args, JsonSerializerOptions json)
    {
        var vaultRoot = RequireOption(args, "--vault");
        var planPath = RequireOption(args, "--plan");
        var backupPath = RequireOption(args, "--backup");
        if (vaultRoot is null || planPath is null || backupPath is null)
            return 2;

        var executor = new ParentMigrationExecutor(vaultRoot, planPath, backupPath, json);
        var result = executor.Rollback(fixtureMode: Array.IndexOf(args, "--fixture") >= 0);
        Console.WriteLine(JsonSerializer.Serialize(result, json));
        return result.Outcome == "rolled_back" ? 0 : 1;
    }

    private static string? RequireOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index >= 0 && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
            return Path.GetFullPath(args[index + 1]);
        Console.Error.WriteLine($"error: parent-migration requires {name} <path>.");
        return null;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(
            "Usage: glasswork-maintenance parent-migration dry-run --vault <root> --ado-evidence <file> --plan <file>");
        return 2;
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    [GeneratedRegex(@"(?m)^type:[ \t]*pbi[ \t]*\r?$", RegexOptions.IgnoreCase)]
    internal static partial Regex LegacyPbiRegex();

    [GeneratedRegex(@"\A---[ \t]*\r?\n(?<yaml>.*?)(?:\r?\n)---[ \t]*\r?(?:\n|\z)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterBlockRegex();

    internal static bool IsLegacyPbi(string content)
    {
        var match = FrontmatterBlockRegex().Match(content);
        return match.Success && LegacyPbiRegex().IsMatch(match.Groups["yaml"].Value);
    }

    internal static string ComputePlanHash(ParentMigrationPlan plan) =>
        ParentMigrationPlanner.Hash(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(plan with { PlanHash = string.Empty })));
}

internal sealed class ParentMigrationPlanner
{
    private readonly string _vaultRoot;
    private readonly string _todoPath;
    private readonly string _evidencePath;
    private readonly FrontmatterParser _parser = new();

    public ParentMigrationPlanner(string vaultRoot, string evidencePath)
    {
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _todoPath = Path.Combine(_vaultRoot, "wiki", "todo");
        _evidencePath = Path.GetFullPath(evidencePath);
    }

    public ParentMigrationPlan CreatePlan()
    {
        if (!Directory.Exists(_todoPath))
            throw new DirectoryNotFoundException($"Task directory not found: {_todoPath}");
        if (!File.Exists(_evidencePath))
            throw new FileNotFoundException("ADO evidence file was not found.", _evidencePath);

        var diagnostics = new List<MigrationDiagnostic>();
        var evidenceBytes = File.ReadAllBytes(_evidencePath);
        var evidence = ReadEvidence(evidenceBytes, diagnostics);
        var sources = ReadTasks(diagnostics);
        var readBasis = ReadBasis();
        var transformed = sources.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Task.Clone(),
            StringComparer.Ordinal);
        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        var promotions = new List<MigrationPromotion>();
        var sourceKindLookups = new List<MigrationSourceKindLookup>();
        var unresolved = new List<string>();
        var plannedIds = new HashSet<string>(transformed.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources.Values.Where(source => source.LegacyPbi)
                     .OrderBy(source => source.RelativePath, StringComparer.Ordinal))
        {
            var parent = transformed[source.Task.Id];
            parent.Type = GlassworkTask.Types.Parent;
            changedIds.Add(parent.Id);

            if (source.Ado.Status == AdoIdStatus.Ambiguous)
            {
                sourceKindLookups.Add(new(parent.Id, null, "ambiguous", parent.SourceKind));
                diagnostics.Add(new(
                    "ambiguous_ado_identity",
                    [parent.Id],
                    $"Task '{parent.Id}' has more than one possible ADO identity."));
            }
            else if (source.Ado.Id is int resolvedAdoId
                     && evidence.TryGetValue(parent.Id, out var contradictoryEvidence)
                     && contradictoryEvidence.AdoId != resolvedAdoId)
            {
                sourceKindLookups.Add(new(
                    parent.Id,
                    resolvedAdoId,
                    "mismatch",
                    parent.SourceKind));
                diagnostics.Add(new(
                    "ado_evidence_mismatch",
                    [parent.Id],
                    $"ADO evidence for Task '{parent.Id}' names {contradictoryEvidence.AdoId}, not {resolvedAdoId}."));
            }
            else if (!string.IsNullOrWhiteSpace(parent.SourceKind))
            {
                sourceKindLookups.Add(new(
                    parent.Id,
                    source.Ado.Id,
                    "existing",
                    parent.SourceKind));
            }
            else
            {
                var ado = source.Ado;
                if (ado.Id is int id
                    && evidence.TryGetValue(parent.Id, out var item)
                    && item.AdoId == id)
                {
                    parent.SourceKind = item.SourceKind;
                    sourceKindLookups.Add(new(parent.Id, id, "resolved", parent.SourceKind));
                }
                else
                {
                    unresolved.Add(parent.Id);
                    sourceKindLookups.Add(new(parent.Id, ado.Id, "unresolved", null));
                    if (evidence.TryGetValue(parent.Id, out var mismatch))
                    {
                        diagnostics.Add(new(
                            "ado_evidence_mismatch",
                            [parent.Id],
                            $"ADO evidence for Task '{parent.Id}' names {mismatch.AdoId}, not {ado.Id?.ToString() ?? "no resolvable ADO ID"}."));
                    }
                }
            }

            for (var index = 0; index < source.Task.Subtasks.Count; index++)
            {
                var subtask = source.Task.Subtasks[index];
                var childId = CreateChildId(parent.Id, index + 1, subtask.Text);
                if (!plannedIds.Add(childId))
                {
                    diagnostics.Add(new(
                        "task_id_collision",
                        [parent.Id, childId],
                        $"Promoted child Task ID '{childId}' collides with another Task."));
                    continue;
                }

                var child = CreateChild(parent, subtask, childId, index + 1, diagnostics);
                if (child is null)
                    continue;
                transformed[childId] = child;
                promotions.Add(new(
                    parent.Id,
                    index + 1,
                    childId,
                    subtask.Text,
                    RawSubtaskStatus(subtask),
                    child.Status));
            }

            parent.Subtasks.Clear();
        }

        var hierarchy = new TaskHierarchyPolicy(transformed.Values);
        foreach (var task in transformed.Values)
        {
            var originalParent = task.Parent;
            hierarchy.CanonicalizeParent(task);
            if (!string.Equals(originalParent, task.Parent, StringComparison.Ordinal))
                changedIds.Add(task.Id);
        }
        diagnostics.AddRange(hierarchy.Validate(transformed.Keys).Select(item =>
            new MigrationDiagnostic(item.Code, item.TaskIds, item.Message)));

        var changes = new List<MigrationFileChange>();
        foreach (var id in changedIds.Order(StringComparer.Ordinal))
        {
            if (!sources.TryGetValue(id, out var source))
                continue;
            var updated = Encoding.UTF8.GetBytes(_parser.Serialize(transformed[id]));
            changes.Add(new(
                source.RelativePath,
                "update",
                Hash(source.Bytes),
                Hash(updated),
                Convert.ToBase64String(updated),
                source.LegacyPbi));
        }
        foreach (var promotion in promotions.OrderBy(item => item.ChildId, StringComparer.Ordinal))
        {
            var updated = Encoding.UTF8.GetBytes(_parser.Serialize(transformed[promotion.ChildId]));
            changes.Add(new(
                $"wiki/todo/{promotion.ChildId}.md",
                "create",
                null,
                Hash(updated),
                Convert.ToBase64String(updated),
                false));
        }

        var operationPayload = string.Join(
            '\n',
            changes.OrderBy(change => change.RelativePath, StringComparer.Ordinal)
                .Select(change => $"{change.RelativePath}:{change.OriginalHash}:{change.UpdatedHash}"));
        var operationId = Hash(Encoding.UTF8.GetBytes(operationPayload))[..24];
        var unsigned = new ParentMigrationPlan(
            1,
            operationId,
            _vaultRoot,
            Hash(evidenceBytes),
            readBasis,
            changes,
            promotions,
            sourceKindLookups,
            unresolved.Order(StringComparer.Ordinal).ToArray(),
            diagnostics,
            string.Empty);
        return unsigned with { PlanHash = ParentMigrationCommand.ComputePlanHash(unsigned) };
    }

    private Dictionary<string, AdoEvidence> ReadEvidence(
        byte[] bytes,
        ICollection<MigrationDiagnostic> diagnostics)
    {
        var items = JsonSerializer.Deserialize<List<AdoEvidence>>(
            bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? [];
        var result = new Dictionary<string, AdoEvidence>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.TaskId)
                || item.AdoId <= 0
                || string.IsNullOrWhiteSpace(item.SourceKind)
                || !DateTimeOffset.TryParse(item.RetrievedAt, out _))
            {
                diagnostics.Add(new(
                    "invalid_ado_evidence",
                    string.IsNullOrWhiteSpace(item.TaskId) ? [] : [item.TaskId],
                    "ADO evidence requires task_id, positive ado_id, source_kind, and an RFC 3339 retrieved_at timestamp."));
                continue;
            }
            var normalized = item with
            {
                TaskId = item.TaskId.Trim(),
                SourceKind = item.SourceKind.Trim(),
            };
            if (!result.TryAdd(normalized.TaskId, normalized))
            {
                diagnostics.Add(new(
                    "duplicate_ado_evidence",
                    [normalized.TaskId],
                    $"ADO evidence contains more than one entry for Task '{normalized.TaskId}'."));
            }
        }
        return result;
    }

    private Dictionary<string, TaskSource> ReadTasks(ICollection<MigrationDiagnostic> diagnostics)
    {
        var result = new Dictionary<string, TaskSource>(StringComparer.Ordinal);
        foreach (var (path, relativePath) in EnumerateTaskPaths())
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var content = Encoding.UTF8.GetString(bytes);
                var task = _parser.Parse(content);
                var ado = ResolveAdoIdentity(task, content);
                if (string.IsNullOrWhiteSpace(task.Id))
                    throw new FormatException("Task ID is missing.");
                if (!result.TryAdd(
                    task.Id,
                    new(task, relativePath, bytes, ParentMigrationCommand.IsLegacyPbi(content), ado)))
                {
                    diagnostics.Add(new(
                        "duplicate_task_id",
                        [task.Id],
                        $"Task ID '{task.Id}' appears in more than one file."));
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                diagnostics.Add(new(
                    "task_parse_error",
                    [relativePath],
                    $"Task file '{relativePath}' could not be planned: {ex.Message}"));
            }
        }
        foreach (var collision in result.Keys
                     .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            diagnostics.Add(new(
                "duplicate_task_id",
                collision.Order(StringComparer.Ordinal).ToArray(),
                "Task IDs collide after case-insensitive canonicalization."));
        }
        return result;
    }

    private static AdoIdResolution ResolveAdoIdentity(GlassworkTask task, string content)
    {
        var canonicalLinks = task.Links
            .Where(link => link.Type == TaskLink.Types.Ado)
            .ToArray();
        if (canonicalLinks.Length == 0)
            return TaskTypeBackfillService.ResolveAdoId(content);

        var ids = new HashSet<int>();
        foreach (var link in canonicalLinks)
        {
            var id = AdoParentIdExtractor.TryExtractId(link.Value);
            if (id is null)
                return AdoIdResolution.Ambiguous;
            ids.Add(id.Value);
        }
        return ids.Count == 1
            ? AdoIdResolution.Resolved(ids.Single())
            : AdoIdResolution.Ambiguous;
    }

    private IReadOnlyList<MigrationReadBasisEntry> ReadBasis()
    {
        var paths = EnumerateTaskPaths().Select(item => item.FullPath)
            .Concat(Directory.EnumerateDirectories(_todoPath, "*.artifacts", SearchOption.TopDirectoryOnly)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
        return paths.Select(path => new MigrationReadBasisEntry(
                Path.GetRelativePath(_vaultRoot, path).Replace('\\', '/'),
                Hash(File.ReadAllBytes(path))))
            .ToArray();
    }

    private IEnumerable<(string FullPath, string RelativePath)> EnumerateTaskPaths()
    {
        foreach (var path in Directory.EnumerateFiles(_todoPath, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(path => !Path.GetFileName(path).StartsWith('_'))
                     .Order(StringComparer.Ordinal))
        {
            yield return (path, Path.GetRelativePath(_vaultRoot, path).Replace('\\', '/'));
        }

        var done = Path.Combine(_todoPath, "done");
        if (!Directory.Exists(done))
            yield break;
        foreach (var path in Directory.EnumerateFiles(done, "*.md", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            yield return (path, Path.GetRelativePath(_vaultRoot, path).Replace('\\', '/'));
        }
    }

    private static GlassworkTask? CreateChild(
        GlassworkTask parent,
        SubTask subtask,
        string childId,
        int sourceOrder,
        ICollection<MigrationDiagnostic> diagnostics)
    {
        var rawStatus = RawSubtaskStatus(subtask);
        if (rawStatus is not ("todo" or "in_progress" or "blocked" or "done" or "dropped"))
        {
            diagnostics.Add(new(
                "unsupported_subtask_status",
                [parent.Id, childId],
                $"Subtask {sourceOrder} on Parent Task '{parent.Id}' has unsupported status '{rawStatus}'."));
        }
        if (rawStatus == "blocked"
            && (!subtask.Metadata.TryGetValue("blocker", out var blocker)
                || string.IsNullOrWhiteSpace(blocker)))
        {
            diagnostics.Add(new(
                "blocked_subtask_missing_details",
                [parent.Id, childId],
                $"Blocked Subtask {sourceOrder} on Parent Task '{parent.Id}' has no blocker details."));
            return null;
        }

        var task = new GlassworkTask
        {
            Id = childId,
            Title = subtask.Text,
            Status = rawStatus switch
            {
                "in_progress" => GlassworkTask.Statuses.InProgress,
                "blocked" => GlassworkTask.Statuses.Blocked,
                "done" => GlassworkTask.Statuses.Done,
                "dropped" => GlassworkTask.Statuses.Cancelled,
                _ => GlassworkTask.Statuses.Todo,
            },
            Priority = GlassworkTask.Priorities.Medium,
            Type = GlassworkTask.Types.Task,
            Created = parent.Created,
            CompletedAt = rawStatus == "done" ? ReadCompletedAt(subtask) ?? DateTime.Today : null,
            Parent = parent.Id,
            Notes = subtask.Notes,
            Due = subtask.Due,
            MyDay = ReadMyDay(subtask),
            Size = subtask.Size,
            FrontmatterExtensions = new(StringComparer.Ordinal)
            {
                ["migration"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source_parent"] = parent.Id,
                    ["source_order"] = sourceOrder,
                    ["source_status"] = rawStatus,
                    ["source_checkbox_completed"] = subtask.IsCompleted,
                    ["source_metadata"] = subtask.Metadata.ToDictionary(
                        pair => pair.Key,
                        pair => (object?)pair.Value,
                        StringComparer.Ordinal),
                },
            },
        };
        if (rawStatus == "blocked")
        {
            task.BlockedReason = subtask.Metadata["blocker"];
            task.BlockedAt = DateTimeOffset.UtcNow;
            task.BlockedFromStatus = GlassworkTask.Statuses.Todo;
            task.BlockedMetadataState = BlockedMetadataState.Valid;
        }
        if (rawStatus == "dropped")
        {
            task.CancellationReason = "Promoted from a dropped inline Subtask by the Parent Task migration.";
            task.CancelledAt = DateTimeOffset.UtcNow;
        }
        return task;
    }

    private static DateTime? ReadMyDay(SubTask subtask)
    {
        if (!subtask.Metadata.TryGetValue("my_day", out var raw))
            return null;
        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase))
            return DateTime.Today;
        return DateTime.TryParse(raw, out var value) ? value.Date : null;
    }

    private static DateTime? ReadCompletedAt(SubTask subtask) =>
        subtask.Metadata.TryGetValue("completed", out var raw)
        && DateTime.TryParse(raw, out var completed)
            ? completed.Date
            : null;

    private static string RawSubtaskStatus(SubTask subtask) =>
        string.IsNullOrWhiteSpace(subtask.Status)
            ? subtask.IsCompleted ? "done" : "todo"
            : subtask.Status.Trim();

    private static string CreateChildId(string parentId, int order, string title)
    {
        var hash = Hash(Encoding.UTF8.GetBytes($"{parentId}\n{order}\n{title}"))[..10];
        var stem = VaultService.GenerateId($"{parentId}-subtask-{order:D3}");
        var maxStemLength = 60 - hash.Length - 1;
        if (stem.Length > maxStemLength)
            stem = stem[..maxStemLength].TrimEnd('-');
        return $"{stem}-{hash}";
    }

    internal static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record TaskSource(
        GlassworkTask Task,
        string RelativePath,
        byte[] Bytes,
        bool LegacyPbi,
        AdoIdResolution Ado);
}

internal sealed class ParentMigrationExecutor
{
    private const string JournalFileName = "parent-migration-journal.json";
    private readonly string _vaultRoot;
    private readonly string _todoPath;
    private readonly string _planPath;
    private readonly string _backupPath;
    private readonly JsonSerializerOptions _json;
    private readonly SelfWriteCoordinator _selfWrites;

    public ParentMigrationExecutor(
        string vaultRoot,
        string planPath,
        string backupPath,
        JsonSerializerOptions json)
    {
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _todoPath = Path.Combine(_vaultRoot, "wiki", "todo");
        _planPath = Path.GetFullPath(planPath);
        _backupPath = Path.GetFullPath(backupPath);
        _json = json;
        _selfWrites = new SelfWriteCoordinator(_todoPath);
    }

    public MigrationOperationReport Execute(bool fixtureMode)
    {
        if (IsPathUnder(_backupPath, _vaultRoot))
            return new("blocked", "backup_inside_vault", "Backup directory must be outside the Vault.");
        if (IsPathUnder(_planPath, _vaultRoot))
            return new("blocked", "plan_inside_vault", "Migration plan must be outside the Vault.");
        if (fixtureMode && !IsFixtureVault())
            return new("blocked", "invalid_fixture_mode", "Fixture mode requires a marked temporary fixture Vault.");
        var writers = ActiveWriters(includeRealProcesses: !fixtureMode);
        if (writers.Length > 0)
        {
            return new(
                "blocked",
                "vault_writers_running",
                $"Close all Glasswork and glasswork-mcp processes before execution: {string.Join(", ", writers)}");
        }

        ParentMigrationPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<ParentMigrationPlan>(
                File.ReadAllText(_planPath),
                _json) ?? throw new InvalidDataException("Migration plan is empty.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return new("blocked", "invalid_plan", ex.Message);
        }

        var planError = ValidatePlan(plan);
        if (planError is not null)
            return planError;

        var journalPath = JournalPath;
        if (File.Exists(journalPath))
            return new("blocked", "pending_journal", $"Resolve pending migration journal '{journalPath}' before execution.");

        try
        {
            var backup = CreateBackup(plan);
            var entries = plan.Changes.Select(change => new MigrationJournalEntry(
                change.RelativePath,
                change.Kind,
                change.OriginalHash,
                change.UpdatedHash,
                change.Kind == "update"
                    ? Convert.ToBase64String(File.ReadAllBytes(ResolveVaultPath(change.RelativePath)))
                    : null,
                change.UpdatedBase64)).ToArray();
            var journal = new ParentMigrationJournal(
                1,
                plan.OperationId,
                plan.PlanHash,
                _backupPath,
                false,
                entries);
            ParentMigrationCommand.WriteAtomic(
                journalPath,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal, _json)));

            try
            {
                if (fixtureMode
                    && Environment.GetEnvironmentVariable("GLASSWORK_MIGRATION_TEST_MUTATE_BEFORE_WRITE")
                        is { Length: > 0 } mutatePath)
                {
                    File.AppendAllText(
                        ResolveVaultPath(mutatePath),
                        "\nfixture concurrent edit\n");
                }
                var failAfterWrites = fixtureMode
                    && int.TryParse(
                        Environment.GetEnvironmentVariable("GLASSWORK_MIGRATION_TEST_FAIL_AFTER_WRITES"),
                        out var configuredFailure)
                    && configuredFailure > 0
                        ? configuredFailure
                        : (int?)null;
                var exitAfterWrites = fixtureMode
                    && int.TryParse(
                        Environment.GetEnvironmentVariable("GLASSWORK_MIGRATION_TEST_EXIT_AFTER_WRITES"),
                        out var configuredExit)
                    && configuredExit > 0
                        ? configuredExit
                        : (int?)null;
                var writeCount = 0;
                foreach (var entry in entries)
                {
                    ApplyUpdated(entry);
                    writeCount++;
                    if (writeCount == exitAfterWrites)
                        Environment.Exit(86);
                    if (writeCount == failAfterWrites)
                        throw new IOException($"Injected fixture failure after {writeCount} write(s).");
                }
                VerifyUpdated(entries);
                ParentMigrationCommand.WriteAtomic(
                    journalPath,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal with { Committed = true }, _json)));
                VerifyBackup(backup, plan);
                File.Delete(journalPath);
                return new("applied", null, $"Applied {entries.Length} migration change(s).");
            }
            catch
            {
                RestoreOriginals(journal);
                if (File.Exists(journalPath))
                    File.Delete(journalPath);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("failed", "operation_failed", ex.Message);
        }
    }

    public MigrationValidationReport ValidateApplied()
    {
        var diagnostics = new List<MigrationDiagnostic>();
        ParentMigrationPlan plan;
        try
        {
            plan = LoadPlan();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new("invalid_plan", [], ex.Message));
            return new("invalid", false, diagnostics);
        }

        if (File.Exists(JournalPath))
            diagnostics.Add(new("pending_journal", [JournalPath], "Migration journal is still present."));

        MigrationBackupManifest? backup = null;
        try
        {
            var manifestPath = Path.Combine(_backupPath, "manifest.json");
            backup = JsonSerializer.Deserialize<MigrationBackupManifest>(
                File.ReadAllText(manifestPath),
                _json) ?? throw new InvalidDataException("Backup manifest is empty.");
            if (backup.OperationId != plan.OperationId || backup.PlanHash != plan.PlanHash)
                throw new InvalidDataException("Backup manifest does not match the migration plan.");
            VerifyBackup(backup, plan);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new("backup_invalid", [_backupPath], ex.Message));
        }

        foreach (var change in plan.Changes)
        {
            var path = ResolveVaultPath(change.RelativePath);
            if (!File.Exists(path)
                || !string.Equals(HashFile(path), change.UpdatedHash, StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    "post_migration_hash_mismatch",
                    [change.RelativePath],
                    $"Post-migration bytes do not match the accepted plan for '{change.RelativePath}'."));
            }
        }

        var changedPaths = plan.Changes
            .Select(change => change.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var basis in plan.ReadBasis.Where(item => !changedPaths.Contains(item.RelativePath)))
        {
            var path = ResolveVaultPath(basis.RelativePath);
            if (!File.Exists(path)
                || !string.Equals(HashFile(path), basis.Sha256, StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    "unchanged_path_drift",
                    [basis.RelativePath],
                    $"Unchanged Task or Artifact path '{basis.RelativePath}' drifted during migration."));
            }
        }

        var tasks = new List<GlassworkTask>();
        var parser = new FrontmatterParser();
        foreach (var path in EnumerateTaskFiles())
        {
            try
            {
                var task = parser.Parse(File.ReadAllText(path));
                var roundTripped = parser.Parse(parser.Serialize(task));
                if (task.Id != roundTripped.Id
                    || task.Type != roundTripped.Type
                    || task.SourceKind != roundTripped.SourceKind
                    || task.Parent != roundTripped.Parent
                    || task.Subtasks.Count != roundTripped.Subtasks.Count)
                {
                    diagnostics.Add(new(
                        "round_trip_mismatch",
                        [Path.GetRelativePath(_vaultRoot, path).Replace('\\', '/')],
                        "Task parse/serialize/parse semantics are not stable."));
                }
                tasks.Add(task);
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                diagnostics.Add(new(
                    "task_parse_error",
                    [Path.GetRelativePath(_vaultRoot, path).Replace('\\', '/')],
                    ex.Message));
            }
        }

        var hasDuplicateIds = tasks
            .Select(task => task.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != tasks.Count;
        if (hasDuplicateIds)
            diagnostics.Add(new("duplicate_task_id", [], "Task IDs collide after case-insensitive canonicalization."));

        var byId = hasDuplicateIds
            ? new Dictionary<string, GlassworkTask>(StringComparer.Ordinal)
            : tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        if (!hasDuplicateIds)
        {
            var hierarchy = new TaskHierarchyPolicy(tasks);
            diagnostics.AddRange(hierarchy.Validate(tasks.Select(task => task.Id)).Select(item =>
                new MigrationDiagnostic(item.Code, item.TaskIds, item.Message)));
        }
        foreach (var promotion in plan.Promotions)
        {
            if (!byId.TryGetValue(promotion.ChildId, out var child)
                || child.Parent != promotion.ParentId
                || child.Title != promotion.Title
                || child.Status != promotion.TaskStatus)
            {
                diagnostics.Add(new(
                    "promotion_mismatch",
                    [promotion.ParentId, promotion.ChildId],
                    $"Promoted child '{promotion.ChildId}' does not match the accepted plan."));
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     _todoPath,
                     "*.artifacts",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            var ownerId = name[..^".artifacts".Length];
            if (!byId.ContainsKey(ownerId))
            {
                diagnostics.Add(new(
                    "orphan_artifact_directory",
                    [Path.GetRelativePath(_vaultRoot, directory).Replace('\\', '/')],
                    $"Artifact directory owner Task '{ownerId}' does not exist."));
            }
        }

        return new(
            diagnostics.Count == 0 ? "valid" : "invalid",
            backup is not null && diagnostics.Count == 0,
            diagnostics);
    }

    public MigrationOperationReport Rollback(bool fixtureMode)
    {
        if (fixtureMode && !IsFixtureVault())
            return new("blocked", "invalid_fixture_mode", "Fixture mode requires a marked temporary fixture Vault.");
        if (ActiveWriters(includeRealProcesses: !fixtureMode) is { Length: > 0 } writers)
        {
            return new(
                "blocked",
                "vault_writers_running",
                $"Close all Glasswork and glasswork-mcp processes before rollback: {string.Join(", ", writers)}");
        }

        ParentMigrationPlan plan;
        MigrationBackupManifest backup;
        try
        {
            plan = LoadPlan();
            if (File.Exists(JournalPath))
            {
                var journal = JsonSerializer.Deserialize<ParentMigrationJournal>(
                    File.ReadAllText(JournalPath),
                    _json) ?? throw new InvalidDataException("Migration journal is empty.");
                if (journal.OperationId != plan.OperationId
                    || journal.PlanHash != plan.PlanHash
                    || !Path.GetFullPath(journal.BackupPath).Equals(
                        _backupPath,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Migration journal does not match the requested plan and backup.");
                }
            }
            backup = JsonSerializer.Deserialize<MigrationBackupManifest>(
                File.ReadAllText(Path.Combine(_backupPath, "manifest.json")),
                _json) ?? throw new InvalidDataException("Backup manifest is empty.");
            if (backup.OperationId != plan.OperationId || backup.PlanHash != plan.PlanHash)
                throw new InvalidDataException("Backup manifest does not match the migration plan.");
            VerifyBackup(backup, plan);

            foreach (var change in plan.Changes)
            {
                var path = ResolveVaultPath(change.RelativePath);
                var currentHash = File.Exists(path) ? HashFile(path) : null;
                if (change.Kind == "create")
                {
                    if (currentHash is not null
                        && !string.Equals(currentHash, change.UpdatedHash, StringComparison.Ordinal))
                        throw new InvalidDataException($"Rollback refused changed child '{change.RelativePath}'.");
                }
                else if (!string.Equals(currentHash, change.OriginalHash, StringComparison.Ordinal)
                         && !string.Equals(currentHash, change.UpdatedHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Rollback refused changed Task '{change.RelativePath}'.");
                }
            }

            var changedPaths = plan.Changes
                .Select(change => change.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var basis in plan.ReadBasis.Where(item => !changedPaths.Contains(item.RelativePath)))
            {
                var path = ResolveVaultPath(basis.RelativePath);
                if (!File.Exists(path)
                    || !string.Equals(HashFile(path), basis.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Rollback refused drifted path '{basis.RelativePath}'.");
            }

            if (fixtureMode
                && Environment.GetEnvironmentVariable("GLASSWORK_MIGRATION_TEST_MUTATE_BEFORE_ROLLBACK_WRITE")
                    is { Length: > 0 } mutatePath)
            {
                File.AppendAllText(
                    ResolveVaultPath(mutatePath),
                    "\nfixture concurrent edit\n");
            }

            foreach (var change in plan.Changes.Reverse())
            {
                var path = ResolveVaultPath(change.RelativePath);
                var currentHash = File.Exists(path) ? HashFile(path) : null;
                if (change.Kind == "create")
                {
                    if (currentHash is null)
                        continue;
                    if (!string.Equals(currentHash, change.UpdatedHash, StringComparison.Ordinal))
                        throw new InvalidDataException($"Rollback refused changed child '{change.RelativePath}'.");
                    _selfWrites.RegisterWrite(path);
                    File.Delete(path);
                    continue;
                }
                if (string.Equals(currentHash, change.OriginalHash, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(currentHash, change.UpdatedHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Rollback refused changed Task '{change.RelativePath}'.");
                var original = Path.Combine(
                    _backupPath,
                    "originals",
                    change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                _selfWrites.RegisterWrite(path);
                ParentMigrationCommand.WriteAtomic(path, File.ReadAllBytes(original));
            }

            foreach (var basis in plan.ReadBasis)
            {
                var path = ResolveVaultPath(basis.RelativePath);
                if (!File.Exists(path)
                    || !string.Equals(HashFile(path), basis.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Rollback verification failed for '{basis.RelativePath}'.");
            }
            foreach (var change in plan.Changes.Where(change => change.Kind == "create"))
            {
                if (File.Exists(ResolveVaultPath(change.RelativePath)))
                    throw new InvalidDataException($"Rollback left created child '{change.RelativePath}'.");
            }
            if (File.Exists(JournalPath))
                File.Delete(JournalPath);
            return new("rolled_back", null, $"Restored {backup.Entries.Count(entry => entry.Kind == "original")} original file(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new("blocked", "rollback_failed", ex.Message);
        }
    }

    private MigrationOperationReport? ValidatePlan(ParentMigrationPlan plan)
    {
        if (!Path.GetFullPath(plan.VaultRoot)
                .Equals(_vaultRoot, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return new("blocked", "vault_mismatch", "Migration plan was created for a different Vault.");
        }

        if (plan.BlockingDiagnostics.Count > 0)
            return new("blocked", "plan_has_blockers", "Migration plan contains blocking diagnostics.");
        if (!string.Equals(
                plan.PlanHash,
                ParentMigrationCommand.ComputePlanHash(plan),
                StringComparison.Ordinal))
        {
            return new("blocked", "plan_hash_mismatch", "Migration plan hash does not match its content.");
        }
        foreach (var basis in plan.ReadBasis)
        {
            var path = ResolveVaultPath(basis.RelativePath);
            if (!File.Exists(path)
                || !string.Equals(HashFile(path), basis.Sha256, StringComparison.Ordinal))
            {
                return new(
                    "blocked",
                    "read_basis_drift",
                    $"Vault path '{basis.RelativePath}' changed after dry-run.");
            }
        }
        var acceptedPaths = plan.ReadBasis
            .Select(item => item.RelativePath)
            .Concat(plan.Changes
                .Where(change => change.Kind == "create")
                .Select(change => change.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedPath = EnumerateScopedRelativePaths()
            .FirstOrDefault(path => !acceptedPaths.Contains(path));
        if (addedPath is not null)
        {
            return new(
                "blocked",
                "read_basis_drift",
                $"Vault path '{addedPath}' was added after dry-run.");
        }
        foreach (var change in plan.Changes)
        {
            var path = ResolveVaultPath(change.RelativePath);
            if (change.Kind == "create" && File.Exists(path))
                return new("blocked", "created_path_exists", $"Planned child path '{change.RelativePath}' already exists.");
            if (change.Kind == "update"
                && (!File.Exists(path)
                    || !string.Equals(HashFile(path), change.OriginalHash, StringComparison.Ordinal)))
            {
                return new("blocked", "change_drift", $"Planned update path '{change.RelativePath}' changed after dry-run.");
            }
            var updated = Convert.FromBase64String(change.UpdatedBase64);
            if (!string.Equals(
                    ParentMigrationPlanner.Hash(updated),
                    change.UpdatedHash,
                    StringComparison.Ordinal))
            {
                return new("blocked", "updated_hash_mismatch", $"Planned bytes for '{change.RelativePath}' are invalid.");
            }
        }
        return null;
    }

    private ParentMigrationPlan LoadPlan()
    {
        var plan = JsonSerializer.Deserialize<ParentMigrationPlan>(
            File.ReadAllText(_planPath),
            _json) ?? throw new InvalidDataException("Migration plan is empty.");
        if (!Path.GetFullPath(plan.VaultRoot)
                .Equals(_vaultRoot, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            throw new InvalidDataException("Migration plan was created for a different Vault.");
        if (!string.Equals(
                plan.PlanHash,
                ParentMigrationCommand.ComputePlanHash(plan),
                StringComparison.Ordinal))
            throw new InvalidDataException("Migration plan hash does not match its content.");
        return plan;
    }

    private MigrationBackupManifest CreateBackup(ParentMigrationPlan plan)
    {
        var manifestPath = Path.Combine(_backupPath, "manifest.json");
        if (Directory.Exists(_backupPath) && Directory.EnumerateFileSystemEntries(_backupPath).Any())
        {
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("Backup directory is non-empty and has no migration manifest.");
            var existing = JsonSerializer.Deserialize<MigrationBackupManifest>(
                File.ReadAllText(manifestPath),
                _json) ?? throw new InvalidDataException("Backup manifest is empty.");
            if (existing.OperationId != plan.OperationId || existing.PlanHash != plan.PlanHash)
                throw new InvalidDataException("Existing backup belongs to a different migration plan.");
            VerifyBackup(existing, plan);
            return existing;
        }

        Directory.CreateDirectory(_backupPath);
        var entries = new List<MigrationBackupEntry>();
        foreach (var change in plan.Changes)
        {
            if (change.Kind == "create")
            {
                entries.Add(new(change.RelativePath, "absent", null));
                continue;
            }
            var source = ResolveVaultPath(change.RelativePath);
            var backup = Path.Combine(
                _backupPath,
                "originals",
                change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(source, backup, overwrite: false);
            if (!string.Equals(HashFile(backup), change.OriginalHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Backup verification failed for '{change.RelativePath}'.");
            entries.Add(new(change.RelativePath, "original", change.OriginalHash));
        }
        var manifest = new MigrationBackupManifest(1, plan.OperationId, plan.PlanHash, entries);
        ParentMigrationCommand.WriteAtomic(
            manifestPath,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, _json)));
        VerifyBackup(manifest, plan);
        return manifest;
    }

    private void VerifyBackup(MigrationBackupManifest manifest, ParentMigrationPlan plan)
    {
        if (manifest.Entries.Count != plan.Changes.Count)
            throw new InvalidDataException("Backup manifest does not cover the complete migration change set.");
        var entriesByPath = manifest.Entries
            .GroupBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.Changes)
        {
            if (!entriesByPath.TryGetValue(change.RelativePath, out var matches)
                || matches.Length != 1)
                throw new InvalidDataException($"Backup manifest is missing a unique entry for '{change.RelativePath}'.");
            var entry = matches[0];
            if (change.Kind == "update"
                && (entry.Kind != "original"
                    || !string.Equals(entry.Sha256, change.OriginalHash, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"Backup manifest original does not match '{change.RelativePath}'.");
            }
            if (change.Kind == "create"
                && (entry.Kind != "absent" || entry.Sha256 is not null))
            {
                throw new InvalidDataException($"Backup manifest absence marker does not match '{change.RelativePath}'.");
            }
        }

        foreach (var entry in manifest.Entries.Where(entry => entry.Kind == "original"))
        {
            var path = Path.Combine(
                _backupPath,
                "originals",
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)
                || !string.Equals(HashFile(path), entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Backup verification failed for '{entry.RelativePath}'.");
            }
        }
    }

    private void ApplyUpdated(MigrationJournalEntry entry)
    {
        var path = ResolveVaultPath(entry.RelativePath);
        var currentHash = File.Exists(path) ? HashFile(path) : null;
        if (entry.Kind == "create" && currentHash is not null)
            throw new InvalidDataException($"Planned child path '{entry.RelativePath}' appeared before write.");
        if (entry.Kind == "update"
            && !string.Equals(currentHash, entry.OriginalHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Task '{entry.RelativePath}' changed immediately before write.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _selfWrites.RegisterWrite(path);
        ParentMigrationCommand.WriteAtomic(path, Convert.FromBase64String(entry.UpdatedBase64));
    }

    private void VerifyUpdated(IEnumerable<MigrationJournalEntry> entries)
    {
        foreach (var entry in entries)
        {
            var path = ResolveVaultPath(entry.RelativePath);
            if (!File.Exists(path)
                || !string.Equals(HashFile(path), entry.UpdatedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Post-migration verification failed for '{entry.RelativePath}'.");
            }
        }
    }

    private void RestoreOriginals(ParentMigrationJournal journal)
    {
        foreach (var entry in journal.Entries.Reverse())
        {
            var path = ResolveVaultPath(entry.RelativePath);
            var currentHash = File.Exists(path) ? HashFile(path) : null;
            if (entry.Kind == "create")
            {
                if (currentHash is null)
                    continue;
                if (!string.Equals(currentHash, entry.UpdatedHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Rollback refused changed child '{entry.RelativePath}'.");
                _selfWrites.RegisterWrite(path);
                File.Delete(path);
                continue;
            }

            if (currentHash is not null
                && !string.Equals(currentHash, entry.UpdatedHash, StringComparison.Ordinal)
                && !string.Equals(currentHash, entry.OriginalHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Rollback refused changed Task '{entry.RelativePath}'.");
            }
            if (string.Equals(currentHash, entry.OriginalHash, StringComparison.Ordinal))
                continue;
            if (entry.OriginalBase64 is null)
                throw new InvalidDataException($"Rollback data is missing for '{entry.RelativePath}'.");
            _selfWrites.RegisterWrite(path);
            ParentMigrationCommand.WriteAtomic(path, Convert.FromBase64String(entry.OriginalBase64));
        }
    }

    private bool IsFixtureVault()
    {
        var marker = Path.Combine(_vaultRoot, ".glasswork-migration-fixture");
        var temp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return File.Exists(marker)
               && _vaultRoot.StartsWith(
                   temp,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal);
    }

    private string ResolveVaultPath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_vaultRoot, normalized));
        if (!IsPathUnder(fullPath, _vaultRoot))
            throw new InvalidDataException($"Migration path escapes the Vault: '{relativePath}'.");
        return fullPath;
    }

    internal static bool IsPathUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root)
                         .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(
            prefix,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string HashFile(string path) =>
        ParentMigrationPlanner.Hash(File.ReadAllBytes(path));

    private static string[] ActiveWriters(bool includeRealProcesses)
    {
        var writers = includeRealProcesses
            ? Process.GetProcesses()
                .Where(process => process.ProcessName.Equals("Glasswork", StringComparison.OrdinalIgnoreCase)
                                  || process.ProcessName.Equals("glasswork-mcp", StringComparison.OrdinalIgnoreCase))
                .Select(process => $"{process.ProcessName}:{process.Id}")
            : [];
        var fixtureWriters = (Environment.GetEnvironmentVariable(
                "GLASSWORK_MIGRATION_TEST_ADDITIONAL_WRITERS") ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return writers
            .Concat(fixtureWriters)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private string JournalPath => Path.Combine(_todoPath, ".glasswork", JournalFileName);

    private IEnumerable<string> EnumerateTaskFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_todoPath, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(path => !Path.GetFileName(path).StartsWith('_'))
                     .Order(StringComparer.Ordinal))
            yield return path;
        var done = Path.Combine(_todoPath, "done");
        if (!Directory.Exists(done))
            yield break;
        foreach (var path in Directory.EnumerateFiles(done, "*.md", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
            yield return path;
    }

    private IEnumerable<string> EnumerateScopedRelativePaths()
    {
        foreach (var path in EnumerateTaskFiles())
            yield return Path.GetRelativePath(_vaultRoot, path).Replace('\\', '/');
        foreach (var directory in Directory.EnumerateDirectories(
                     _todoPath,
                     "*.artifacts",
                     SearchOption.TopDirectoryOnly))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                yield return Path.GetRelativePath(_vaultRoot, path).Replace('\\', '/');
        }
    }
}

internal sealed record AdoEvidence(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("ado_id")] int AdoId,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("retrieved_at")] string? RetrievedAt);

internal sealed record MigrationReadBasisEntry(string RelativePath, string Sha256);

internal sealed record MigrationFileChange(
    string RelativePath,
    string Kind,
    string? OriginalHash,
    string UpdatedHash,
    string UpdatedBase64,
    bool LegacyParent);

internal sealed record MigrationPromotion(
    string ParentId,
    int SourceOrder,
    string ChildId,
    string Title,
    string SourceStatus,
    string TaskStatus);

internal sealed record MigrationSourceKindLookup(
    string TaskId,
    int? AdoId,
    string Outcome,
    string? SourceKind);

internal sealed record MigrationDiagnostic(
    string Code,
    IReadOnlyList<string> PathsOrTaskIds,
    string Message);

internal sealed record ParentMigrationPlan(
    int SchemaVersion,
    string OperationId,
    string VaultRoot,
    string AdoEvidenceHash,
    IReadOnlyList<MigrationReadBasisEntry> ReadBasis,
    IReadOnlyList<MigrationFileChange> Changes,
    IReadOnlyList<MigrationPromotion> Promotions,
    IReadOnlyList<MigrationSourceKindLookup> SourceKindLookups,
    IReadOnlyList<string> UnresolvedSourceKinds,
    IReadOnlyList<MigrationDiagnostic> BlockingDiagnostics,
    string PlanHash);

internal sealed record ParentMigrationReport(
    string Outcome,
    int ConvertedParentCount,
    int PromotionCount,
    int UnresolvedSourceKindCount,
    IReadOnlyList<MigrationDiagnostic> BlockingDiagnostics,
    string OperationId,
    string PlanHash);
