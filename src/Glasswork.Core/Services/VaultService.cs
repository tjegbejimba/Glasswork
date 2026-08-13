using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Reads and writes GlassworkTask markdown files in the Obsidian vault's todo/ directory.
/// </summary>
public class VaultService
{
    private static readonly Regex SafeTaskIdRegex = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);
    private readonly string _vaultPath;
    private readonly FrontmatterParser _parser = new();
    private readonly SelfWriteCoordinator? _selfWrites;
    private readonly ConcurrentDictionary<string, byte[]> _lastReadBytes = new(StringComparer.Ordinal);
    private Func<IReadOnlyList<string>>? _managedRecovery;
    private Func<IReadOnlyList<string>>? _managedDeleteRecovery;
    private ResourceMutationService? _mutations;
    private readonly object _mutationGate = new();

    public VaultService(string vaultPath) : this(vaultPath, null) { }

    public VaultService(string vaultPath, SelfWriteCoordinator? selfWrites)
    {
        _vaultPath = vaultPath;
        _selfWrites = selfWrites;
        Directory.CreateDirectory(_vaultPath);
    }

    /// <summary>
    /// The root path of the todo vault directory.
    /// </summary>
    public string VaultPath => _vaultPath;

    /// <summary>
    /// Raised after a successful task-file write (Save or any targeted edit).
    /// Argument is the task id. Subscribers may throw — exceptions are caught
    /// and logged so persistence is never affected by indexing or other
    /// downstream failures (issue #184).
    /// </summary>
    public event EventHandler<string>? TaskWritten;

    /// <summary>
    /// Raised after a successful task-file deletion. Argument is the task id.
    /// Same exception-isolation contract as <see cref="TaskWritten"/>.
    /// </summary>
    public event EventHandler<string>? TaskDeleted;

    private void RaiseTaskWritten(string taskId)
    {
        try { TaskWritten?.Invoke(this, taskId); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskWritten subscriber threw for {taskId}: {ex}");
        }
    }

    internal void RegisterManagedRecovery(Func<IReadOnlyList<string>> recovery)
    {
        _managedRecovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
    }

    internal void RegisterManagedDeleteRecovery(Func<IReadOnlyList<string>> recovery)
    {
        _managedDeleteRecovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
    }

    internal void RunManagedRecovery()
    {
        var recoveredTaskIds = _managedRecovery?.Invoke();
        if (recoveredTaskIds is not null)
        {
            foreach (var taskId in recoveredTaskIds.Distinct(StringComparer.Ordinal))
                RaiseTaskWritten(taskId);
        }

        var recoveredDeletes = _managedDeleteRecovery?.Invoke();
        if (recoveredDeletes is null) return;
        foreach (var taskId in recoveredDeletes.Distinct(StringComparer.Ordinal))
            RaiseTaskDeleted(taskId);
    }

    internal void NotifyTaskWritten(string taskId) => RaiseTaskWritten(taskId);
    internal void NotifyTaskDeleted(string taskId) => RaiseTaskDeleted(taskId);

    internal void AttachMutationService(ResourceMutationService mutations)
    {
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
    }

    private ResourceMutationService EnsureMutations()
    {
        if (_mutations is not null) return _mutations;
        lock (_mutationGate)
            return _mutations ??= new ResourceMutationService(_vaultPath, this);
    }

    private void RaiseTaskDeleted(string taskId)
    {
        try { TaskDeleted?.Invoke(this, taskId); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TaskDeleted subscriber threw for {taskId}: {ex}");
        }
    }

    /// <summary>
    /// Load all tasks from the vault directory.
    /// Skips files starting with _ (index, today, schema).
    /// </summary>
    public List<GlassworkTask> LoadAll() =>
        ReadAllSnapshot(tasks => tasks.ToList());

    /// <summary>
    /// Builds a result from one managed Task snapshot while its shared Vault
    /// lease is still held. Internal adapters use this to sample adjunct Vault
    /// indexes without allowing a cooperating Task writer between the reads.
    /// </summary>
    internal TResult ReadAllSnapshot<TResult>(
        Func<IReadOnlyList<GlassworkTask>, TResult> createResult)
    {
        ArgumentNullException.ThrowIfNull(createResult);
        RunManagedRecovery();
        using var lease = VaultScopedCoordinator.EnterShared(_vaultPath);
        var tasks = new List<GlassworkTask>();
        var files = Directory.GetFiles(_vaultPath, "*.md")
            .Where(f => !Path.GetFileName(f).StartsWith('_'));

        foreach (var file in files)
        {
            try
            {
                var bytes = File.ReadAllBytes(file);
                var content = Encoding.UTF8.GetString(bytes);
                var task = _parser.Parse(content);
                task.ResourceRevision = ResourceMutationService.Revision(bytes);
                _lastReadBytes[Path.GetFileNameWithoutExtension(file)] = bytes;
                tasks.Add(task);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse {file}: {ex.Message}");
            }
        }

        return createResult(tasks);
    }

    /// <summary>
    /// Load a single task by its ID.
    /// </summary>
    public GlassworkTask? Load(string taskId)
    {
        RunManagedRecovery();
        using var lease = VaultScopedCoordinator.EnterShared(_vaultPath);
        var filePath = GetFilePath(taskId);
        if (!File.Exists(filePath)) return null;

        var bytes = File.ReadAllBytes(filePath);
        var task = _parser.Parse(Encoding.UTF8.GetString(bytes));
        task.ResourceRevision = ResourceMutationService.Revision(bytes);
        _lastReadBytes[taskId] = bytes;
        return task;
    }

    /// <summary>
    /// Returns the exact bytes used by the most recent successful read of a task.
    /// </summary>
    public byte[] TryGetLastReadBytes(string taskId)
    {
        RunManagedRecovery();
        using var lease = VaultScopedCoordinator.EnterShared(_vaultPath);
        if (_lastReadBytes.TryGetValue(taskId, out var bytes))
            return bytes;

        var filePath = GetFilePath(taskId);
        return File.ReadAllBytes(filePath);
    }

    /// <summary>
    /// Targeted edit: toggle a single subtask checkbox on disk by replacing only the
    /// `### [ ]` / `### [x]` character on the matching line. The rest of the file is left
    /// byte-for-byte untouched. No-op if the title is not found.
    /// </summary>
    public void UpdateSubtaskCheckbox(string taskId, string subtaskTitle, bool isCompleted)
    {
        RunManagedRecovery();
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        var newCheck = isCompleted ? "x" : " ";
        var pattern = @"^### \[[ xX]\] " + System.Text.RegularExpressions.Regex.Escape(subtaskTitle) + @"\s*$";
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Multiline);

        var match = regex.Match(content);
        if (!match.Success) return;

        var updated = content[..match.Index] + $"### [{newCheck}] {subtaskTitle}" + content[(match.Index + match.Length)..];
        if (updated != content)
        {
            CommitManagedBytes(taskId, updated, originalBytes);
        }
    }

    /// <summary>
    /// Targeted edit: set or clear a subtask's <c>my_day</c> metadata flag without rewriting
    /// the rest of the file. When enabling, inserts <c>- my_day: true</c> at the end of the
    /// subtask's metadata block (immediately after any existing <c>- key: value</c> lines under
    /// the matching <c>### [ ]</c> header). When disabling, removes the existing line if present.
    /// No-op if the title is not found or the flag is already in the desired state.
    /// </summary>
    public void SetSubtaskMyDay(string taskId, string subtaskTitle, bool isMyDay)
    {
        RunManagedRecovery();
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        // Detect the line ending used by the file so we don't mix \n and \r\n.
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var headerPattern = new System.Text.RegularExpressions.Regex(
            @"^### \[[ xX]\] " + System.Text.RegularExpressions.Regex.Escape(subtaskTitle) + @"\s*$");
        var metaLinePattern = new System.Text.RegularExpressions.Regex(@"^- ([a-z_][a-z0-9_]*): (.*)$");

        int headerIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (headerPattern.IsMatch(lines[i]))
            {
                headerIdx = i;
                break;
            }
        }
        if (headerIdx < 0) return;

        // Walk metadata block following the header.
        int metaEnd = headerIdx; // last index that is part of metadata (inclusive of header)
        int existingMyDayIdx = -1;
        for (int i = headerIdx + 1; i < lines.Count; i++)
        {
            var m = metaLinePattern.Match(lines[i]);
            if (!m.Success) break;
            metaEnd = i;
            if (m.Groups[1].Value == "my_day") existingMyDayIdx = i;
        }

        if (isMyDay)
        {
            if (existingMyDayIdx >= 0) return; // already set, no-op
            lines.Insert(metaEnd + 1, "- my_day: true");
        }
        else
        {
            if (existingMyDayIdx < 0) return; // not present, no-op
            lines.RemoveAt(existingMyDayIdx);
        }

        // Preserve trailing newline state by checking the original content.
        var hadTrailingNewline = content.EndsWith('\n');
        var rebuilt = string.Join(newline, lines);
        if (hadTrailingNewline && !rebuilt.EndsWith(newline))
            rebuilt += newline;
        CommitManagedBytes(taskId, rebuilt, originalBytes);
    }

    /// <summary>
    /// Targeted edit: set or clear the <c>due</c> metadata on a subtask without rewriting
    /// the rest of the file. When <paramref name="due"/> is non-null, inserts or replaces
    /// the <c>- due: yyyy-MM-dd</c> line in the subtask's metadata block (immediately after
    /// the matching <c>### [ ]</c> header, before any other metadata lines if no due exists,
    /// or in-place if one does). When null, removes the existing line if present.
    /// No-op if the title is not found or the value is already in the desired state.
    /// </summary>
    public void SetSubtaskDue(string taskId, string subtaskTitle, DateTime? due)
    {
        RunManagedRecovery();
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var headerPattern = new System.Text.RegularExpressions.Regex(
            @"^### \[[ xX]\] " + System.Text.RegularExpressions.Regex.Escape(subtaskTitle) + @"\s*$");
        var metaLinePattern = new System.Text.RegularExpressions.Regex(@"^- ([a-z_][a-z0-9_]*): (.*)$");

        int headerIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (headerPattern.IsMatch(lines[i]))
            {
                headerIdx = i;
                break;
            }
        }
        if (headerIdx < 0) return;

        int metaEnd = headerIdx;
        int existingDueIdx = -1;
        for (int i = headerIdx + 1; i < lines.Count; i++)
        {
            var m = metaLinePattern.Match(lines[i]);
            if (!m.Success) break;
            metaEnd = i;
            if (m.Groups[1].Value == "due") existingDueIdx = i;
        }

        if (due is { } d)
        {
            var newLine = $"- due: {d:yyyy-MM-dd}";
            if (existingDueIdx >= 0)
            {
                if (lines[existingDueIdx] == newLine) return;
                lines[existingDueIdx] = newLine;
            }
            else
            {
                lines.Insert(metaEnd + 1, newLine);
            }
        }
        else
        {
            if (existingDueIdx < 0) return;
            lines.RemoveAt(existingDueIdx);
        }

        var hadTrailingNewline = content.EndsWith('\n');
        var rebuilt = string.Join(newline, lines);
        if (hadTrailingNewline && !rebuilt.EndsWith(newline))
            rebuilt += newline;
        CommitManagedBytes(taskId, rebuilt, originalBytes);
    }

    /// <summary>
    /// <summary>
    /// Targeted edit: set or clear the <c>ado_link</c>/<c>ado_title</c> frontmatter keys without
    /// rewriting any other frontmatter keys or the body. When <paramref name="adoId"/> is non-null,
    /// inserts or replaces the <c>ado_link:</c> line (and <c>ado_title:</c> when supplied). When
    /// <paramref name="adoId"/> is null, removes both lines if present. Preserves line endings
    /// (\n vs \r\n) and the rest of the file byte-for-byte. No-op if the file does not exist.
    /// </summary>
    public void SetAdoLink(string taskId, int? adoId, string? adoTitle)
    {
        RunManagedRecovery();
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";

        // Locate the frontmatter block: opening "---" line through the next "---" line.
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != "---") return;

        int closeIdx = -1;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i] == "---") { closeIdx = i; break; }
        }
        if (closeIdx < 0) return;

        // Strip any existing ado_link/ado_title lines inside the frontmatter block,
        // remembering where the first one was so we can re-insert at the same position.
        var adoLinkPattern = new System.Text.RegularExpressions.Regex(@"^ado_link:\s.*$");
        var adoTitlePattern = new System.Text.RegularExpressions.Regex(@"^ado_title:\s.*$");
        var newFront = new List<string>(closeIdx - 1);
        int insertPos = -1;
        for (int i = 1; i < closeIdx; i++)
        {
            if (adoLinkPattern.IsMatch(lines[i]) || adoTitlePattern.IsMatch(lines[i]))
            {
                if (insertPos < 0) insertPos = newFront.Count;
                continue;
            }
            newFront.Add(lines[i]);
        }
        if (insertPos < 0) insertPos = newFront.Count; // append at end if none existed

        if (adoId.HasValue)
        {
            var inserted = new List<string> { $"ado_link: {adoId.Value}" };
            if (!string.IsNullOrWhiteSpace(adoTitle))
                inserted.Add($"ado_title: {adoTitle}");
            newFront.InsertRange(insertPos, inserted);
        }

        // Rebuild: opening "---", new frontmatter lines, closing "---", everything after.
        var rebuilt = new List<string> { "---" };
        rebuilt.AddRange(newFront);
        rebuilt.Add("---");
        for (int i = closeIdx + 1; i < lines.Count; i++) rebuilt.Add(lines[i]);

        var hadTrailingNewline = content.EndsWith('\n');
        var output = string.Join(newline, rebuilt);
        if (hadTrailingNewline && !output.EndsWith(newline))
            output += newline;

        if (output == content) return;

        CommitManagedBytes(taskId, output, originalBytes);
    }

    /// <summary>
    /// Targeted edit: set or clear the <c>parent</c> frontmatter key without rewriting any
    /// other frontmatter keys or the body. Empty/whitespace value removes the line. Preserves
    /// line endings and the rest of the file byte-for-byte. No-op if the file does not exist.
    /// </summary>
    public void SetParent(string taskId, string? parent)
    {
        RunManagedRecovery();
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";

        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != "---") return;

        int closeIdx = -1;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i] == "---") { closeIdx = i; break; }
        }
        if (closeIdx < 0) return;

        var parentPattern = new System.Text.RegularExpressions.Regex(@"^parent:\s.*$");
        var newFront = new List<string>(closeIdx - 1);
        int insertPos = -1;
        for (int i = 1; i < closeIdx; i++)
        {
            if (parentPattern.IsMatch(lines[i]))
            {
                if (insertPos < 0) insertPos = newFront.Count;
                continue;
            }
            newFront.Add(lines[i]);
        }
        if (insertPos < 0) insertPos = newFront.Count;

        var trimmed = parent?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            newFront.Insert(insertPos, $"parent: {trimmed}");
        }

        var rebuilt = new List<string> { "---" };
        rebuilt.AddRange(newFront);
        rebuilt.Add("---");
        for (int i = closeIdx + 1; i < lines.Count; i++) rebuilt.Add(lines[i]);

        var hadTrailingNewline = content.EndsWith('\n');
        var output = string.Join(newline, rebuilt);
        if (hadTrailingNewline && !output.EndsWith(newline))
            output += newline;

        if (output == content) return;

        CommitManagedBytes(taskId, output, originalBytes);
    }

    /// <summary>
    /// Targeted edit: set the task-level <c>my_day</c> frontmatter key without
    /// rewriting the task body. Preserves line endings and existing frontmatter
    /// order when replacing an existing value.
    /// </summary>
    public void SetMyDay(string taskId, DateTime myDay)
    {
        RunManagedRecovery();
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";

        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != "---") return;

        int closeIdx = -1;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i] == "---") { closeIdx = i; break; }
        }
        if (closeIdx < 0) return;

        var newLine = $"my_day: {myDay:yyyy-MM-dd}";
        var myDayPattern = new System.Text.RegularExpressions.Regex(@"^my_day:\s.*$");
        var newFront = new List<string>(closeIdx - 1);
        int insertPos = -1;
        for (int i = 1; i < closeIdx; i++)
        {
            if (myDayPattern.IsMatch(lines[i]))
            {
                if (insertPos < 0) insertPos = newFront.Count;
                continue;
            }
            newFront.Add(lines[i]);
        }
        if (insertPos < 0) insertPos = newFront.Count;
        newFront.Insert(insertPos, newLine);

        var rebuilt = new List<string> { "---" };
        rebuilt.AddRange(newFront);
        rebuilt.Add("---");
        for (int i = closeIdx + 1; i < lines.Count; i++) rebuilt.Add(lines[i]);

        var hadTrailingNewline = content.EndsWith('\n');
        var output = string.Join(newline, rebuilt);
        if (hadTrailingNewline && !output.EndsWith(newline))
            output += newline;

        if (output == content) return;

        CommitManagedBytes(taskId, output, originalBytes);
    }

    /// <summary>
    /// Targeted edit: toggle the task-level <c>my_day</c> frontmatter key.
    /// When <paramref name="inMyDay"/> is true, sets to today. When false, removes the field.
    /// </summary>
    public void ToggleMyDay(string taskId, bool inMyDay)
    {
        RunManagedRecovery();
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        if (inMyDay)
        {
            SetMyDay(taskId, DateTime.Today);
            return;
        }

        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";

        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != "---") return;

        int closeIdx = -1;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i] == "---") { closeIdx = i; break; }
        }
        if (closeIdx < 0) return;

        var myDayPattern = new System.Text.RegularExpressions.Regex(@"^my_day:\s.*$");
        var newFront = new List<string>(closeIdx - 1);
        for (int i = 1; i < closeIdx; i++)
        {
            if (myDayPattern.IsMatch(lines[i])) continue;
            newFront.Add(lines[i]);
        }

        var rebuilt = new List<string> { "---" };
        rebuilt.AddRange(newFront);
        rebuilt.Add("---");
        for (int i = closeIdx + 1; i < lines.Count; i++) rebuilt.Add(lines[i]);

        var hadTrailingNewline = content.EndsWith('\n');
        var output = string.Join(newline, rebuilt);
        if (hadTrailingNewline && !output.EndsWith(newline))
            output += newline;

        if (output == content) return;

        CommitManagedBytes(taskId, output, originalBytes);
    }

    /// <summary>
    /// Targeted edit: append a new <c>### [ ] {title}</c> subtask under the <c>## Subtasks</c>
    /// section. Creates the section at end of file if missing. New subtask is placed at the
    /// bottom of the active group — immediately before the first "completed" subtask
    /// (header marked <c>[x]</c>, or whose metadata block sets <c>status: done</c> /
    /// <c>status: dropped</c>). Preserves line endings, surrounding prose, and metadata
    /// blocks of other subtasks. No-op when the title is whitespace or the file is missing.
    /// </summary>
    public void AddSubtask(string taskId, string title)
    {
        RunManagedRecovery();
        if (string.IsNullOrWhiteSpace(title)) return;

        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var trimmed = title.Trim();
        var originalBytes = File.ReadAllBytes(path);
        var content = Encoding.UTF8.GetString(originalBytes);
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var hadTrailingNewline = content.EndsWith('\n');

        var subtasksHeaderIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "## Subtasks")
            {
                subtasksHeaderIdx = i;
                break;
            }
        }

        var headerPattern = new System.Text.RegularExpressions.Regex(@"^### \[([ xX])\] (.+?)\s*$");
        var metaPattern = new System.Text.RegularExpressions.Regex(@"^- ([a-z_][a-z0-9_]*): (.*)$");
        var newSubtaskLine = $"### [ ] {trimmed}";

        if (subtasksHeaderIdx < 0)
        {
            // No section — append it (with a blank-line separator) at end of file.
            // Strip trailing blanks first to avoid stacking blank lines.
            while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1])) lines.RemoveAt(lines.Count - 1);
            lines.Add("");
            lines.Add("## Subtasks");
            lines.Add("");
            lines.Add(newSubtaskLine);
        }
        else
        {
            // Find section bounds: from header to next `## ` heading or EOF.
            int sectionEnd = lines.Count;
            for (int i = subtasksHeaderIdx + 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("## ") && !lines[i].StartsWith("### "))
                {
                    sectionEnd = i;
                    break;
                }
            }

            // Walk subtasks and find the first "completed" header within the section.
            int insertAt = -1;
            int lastActiveBlockEnd = -1; // last line index that belongs to an active subtask
            int i2 = subtasksHeaderIdx + 1;
            while (i2 < sectionEnd)
            {
                var hm = headerPattern.Match(lines[i2]);
                if (!hm.Success) { i2++; continue; }

                var checkChar = hm.Groups[1].Value;

                // Walk this subtask's metadata block.
                int blockEnd = i2;
                string? statusValue = null;
                for (int j = i2 + 1; j < sectionEnd; j++)
                {
                    var mm = metaPattern.Match(lines[j]);
                    if (!mm.Success) break;
                    blockEnd = j;
                    if (mm.Groups[1].Value == "status") statusValue = mm.Groups[2].Value.Trim();
                }

                bool isDone = checkChar is "x" or "X"
                    || statusValue is "done" or "dropped";

                if (isDone)
                {
                    insertAt = i2;
                    break;
                }
                lastActiveBlockEnd = blockEnd;
                i2 = blockEnd + 1;
            }

            if (insertAt < 0)
            {
                // No completed subtask — insert after last active block, or after the section
                // header (skipping any blank line) if there are no subtasks at all.
                if (lastActiveBlockEnd >= 0)
                {
                    lines.Insert(lastActiveBlockEnd + 1, newSubtaskLine);
                }
                else
                {
                    // Empty section: insert after `## Subtasks` and its conventional blank line.
                    int insertPoint = subtasksHeaderIdx + 1;
                    if (insertPoint < lines.Count && string.IsNullOrEmpty(lines[insertPoint]))
                        insertPoint++;
                    lines.Insert(insertPoint, newSubtaskLine);
                }
            }
            else
            {
                lines.Insert(insertAt, newSubtaskLine);
            }
        }

        var rebuilt = string.Join(newline, lines);
        if (hadTrailingNewline && !rebuilt.EndsWith(newline))
            rebuilt += newline;

        CommitManagedBytes(taskId, rebuilt, originalBytes);
    }

    /// <summary>
    /// Save a task to disk. Creates or overwrites the file.
    /// </summary>
    public void Save(GlassworkTask task, bool ifAbsent = false)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new ArgumentException("Task must have an ID before saving.");

        EnsureMutations().CommitTask(task, ifAbsent);
    }

    private void CommitManagedBytes(string taskId, string content, byte[]? expectedOriginal = null)
    {
        CommitManagedBytes(taskId, Encoding.UTF8.GetBytes(content), expectedOriginal);
    }

    private void CommitManagedBytes(string taskId, byte[] content, byte[]? expectedOriginal = null)
    {
        EnsureMutations().CommitBytes(taskId, content, expectedOriginal);
    }

    /// <summary>
    /// Replace the full structured links list for a task and persist to disk.
    /// Loads the task, replaces the Links collection, then re-serializes via <see cref="Save"/>.
    /// No-op when the task file does not exist.
    /// </summary>
    public void SetLinks(string taskId, IReadOnlyList<TaskLink> links)
    {
        var task = Load(taskId);
        if (task is null) return;
        task.Links = links.ToList();
        Save(task);
    }

    /// <summary>
    /// Move a subtask from one position to another within the parent task's full subtask list,
    /// then re-serialize the file. No-op when indices are equal, out of range, or the task does
    /// not exist. Indices refer to <see cref="GlassworkTask.Subtasks"/> as parsed (full list,
    /// not a UI-filtered subset).
    /// </summary>
    public void ReorderSubtask(string taskId, int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        var task = Load(taskId);
        if (task is null) return;
        if (fromIndex < 0 || fromIndex >= task.Subtasks.Count) return;
        if (toIndex < 0 || toIndex >= task.Subtasks.Count) return;

        var item = task.Subtasks[fromIndex];
        task.Subtasks.RemoveAt(fromIndex);
        task.Subtasks.Insert(toIndex, item);
        Save(task);
    }

    /// <summary>
    /// In-place V1 → V2 migration of a task file. Reads the raw file, runs
    /// <see cref="MigrationService.MigrateToV2"/>, and writes the result back.
    /// Idempotent and lossless. Returns true if the file was changed on disk.
    /// </summary>
    public bool MigrateToV2(string taskId)
    {
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return false;

        var original = File.ReadAllText(path);
        var migrated = new MigrationService().MigrateToV2(original);
        if (migrated == original) return false;

        // Legacy bootstrap migration predates the managed mutation contract.
        _selfWrites?.RegisterWrite(path);
        File.WriteAllText(path, migrated);
        RaiseTaskWritten(taskId);
        return true;
    }

    /// <summary>
    /// Bulk-migrate every V1 file in the vault to V2 in-place. Idempotent:
    /// V2 files are skipped (zero-cost). Returns the number of files actually
    /// rewritten on disk. Intended to run once at app startup so the user
    /// never sees the per-task "Upgrade to V2" affordance.
    /// </summary>
    public int MigrateAllToV2()
    {
        if (!Directory.Exists(_vaultPath)) return 0;

        var migrated = 0;
        var migrator = new MigrationService();
        foreach (var path in Directory.EnumerateFiles(_vaultPath, "*.md", SearchOption.TopDirectoryOnly))
        {
            string original;
            try { original = File.ReadAllText(path); }
            catch { continue; }

            string migratedContent;
            try { migratedContent = migrator.MigrateToV2(original); }
            catch { continue; } // missing frontmatter etc — leave file untouched

            if (migratedContent == original) continue;

            // Legacy bootstrap migration predates the managed mutation contract.
            _selfWrites?.RegisterWrite(path);
            File.WriteAllText(path, migratedContent);
            RaiseTaskWritten(Path.GetFileNameWithoutExtension(path));
            migrated++;
        }
        return migrated;
    }

    /// <summary>
    /// Delete a task file by ID.
    /// </summary>
    public bool Delete(string taskId)
    {
        return EnsureMutations().CommitDelete(taskId);
    }

    /// <summary>
    /// Check if a task file exists.
    /// </summary>
    public bool Exists(string taskId)
    {
        RunManagedRecovery();
        using var lease = VaultScopedCoordinator.EnterShared(_vaultPath);
        return File.Exists(GetFilePath(taskId));
    }

    public byte[]? TryReadBytes(string taskId)
    {
        RunManagedRecovery();
        using var lease = VaultScopedCoordinator.EnterShared(_vaultPath);
        return TryReadBytesUnsafe(taskId);
    }

    internal byte[]? TryReadBytesUnsafe(string taskId)
    {
        var path = GetFilePath(taskId);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    internal byte[]? TryGetCachedReadBytes(string taskId) =>
        _lastReadBytes.TryGetValue(taskId, out var bytes) ? bytes : null;

    internal void RememberManagedBytes(string taskId, byte[] bytes) =>
        _lastReadBytes[taskId] = bytes;

    internal void ForgetManagedBytes(string taskId) =>
        _lastReadBytes.TryRemove(taskId, out _);

    internal bool ReplaceOwnedFileUnsafe(string path, byte[] bytes, bool overwrite)
    {
        var fullPath = Path.GetFullPath(path);
        var vaultPrefix = Path.GetFullPath(_vaultPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(vaultPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Owned file must be inside the Task vault.", nameof(path));
        if (!overwrite && File.Exists(fullPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _selfWrites?.RegisterWrite(fullPath);
        var tempPath = fullPath + $".mutation.tmp.{Guid.NewGuid():N}";
        File.WriteAllBytes(tempPath, bytes);
        if (File.Exists(fullPath))
            File.Replace(tempPath, fullPath, null);
        else
            File.Move(tempPath, fullPath);
        return true;
    }

    internal byte[]? TryReadOwnedBytesUnsafe(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var vaultPrefix = Path.GetFullPath(_vaultPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(vaultPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Owned file must be inside the Task vault.", nameof(path));
        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    internal void DeleteOwnedFileUnsafe(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var vaultPrefix = Path.GetFullPath(_vaultPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(vaultPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Owned file must be inside the Task vault.", nameof(path));
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    public void ReplaceBytes(string taskId, byte[] bytes)
    {
        CommitManagedBytes(taskId, bytes);
    }

    internal void ReplaceBytesUnsafe(string taskId, byte[] bytes)
    {
        var path = GetFilePath(taskId);
        _selfWrites?.RegisterWrite(path);
        var temp = path + ".mutation.tmp";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }

    internal void DeleteUnsafe(string taskId)
    {
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;
        _selfWrites?.RegisterWrite(path);
        File.Delete(path);
    }

    /// <summary>
    /// Generate a slug-style ID from a title.
    /// </summary>
    public static string GenerateId(string title)
    {
        var slug = title.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        // Remove non-alphanumeric except hyphens
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[^a-z0-9\-]", "");
        // Collapse multiple hyphens
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"-{2,}", "-");
        slug = slug.Trim('-');

        // Truncate to reasonable length
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');

        return slug;
    }

    private string GetFilePath(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task id is required.", nameof(taskId));

        var trimmed = taskId.Trim();
        if (!SafeTaskIdRegex.IsMatch(trimmed))
            throw new ArgumentException($"Task id '{taskId}' is invalid.", nameof(taskId));

        var absoluteVault = Path.GetFullPath(_vaultPath);
        var resolved = Path.GetFullPath(Path.Combine(absoluteVault, $"{trimmed}.md"));
        var vaultPrefix = absoluteVault.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolved.StartsWith(vaultPrefix, comparison))
            throw new ArgumentException($"Task id '{taskId}' resolves outside the vault.", nameof(taskId));

        return resolved;
    }
}
