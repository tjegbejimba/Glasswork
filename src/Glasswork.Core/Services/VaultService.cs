using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Reads and writes GlassworkTask markdown files in the Obsidian vault's todo/ directory.
/// </summary>
public class VaultService
{
    private readonly string _vaultPath;
    private readonly FrontmatterParser _parser = new();
    private readonly SelfWriteCoordinator? _selfWrites;

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
    /// Load all tasks from the vault directory.
    /// Skips files starting with _ (index, today, schema).
    /// </summary>
    public List<GlassworkTask> LoadAll()
    {
        var tasks = new List<GlassworkTask>();
        var files = Directory.GetFiles(_vaultPath, "*.md")
            .Where(f => !Path.GetFileName(f).StartsWith('_'));

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var task = _parser.Parse(content);
                tasks.Add(task);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse {file}: {ex.Message}");
            }
        }

        return tasks;
    }

    /// <summary>
    /// Load a single task by its ID.
    /// </summary>
    public GlassworkTask? Load(string taskId)
    {
        var filePath = GetFilePath(taskId);
        if (!File.Exists(filePath)) return null;

        var content = File.ReadAllText(filePath);
        return _parser.Parse(content);
    }

    /// <summary>
    /// Targeted edit: toggle a single subtask checkbox on disk by replacing only the
    /// `### [ ]` / `### [x]` character on the matching line. The rest of the file is left
    /// byte-for-byte untouched. No-op if the title is not found.
    /// </summary>
    public void UpdateSubtaskCheckbox(string taskId, string subtaskTitle, bool isCompleted)
    {
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var content = File.ReadAllText(path);
        var newCheck = isCompleted ? "x" : " ";
        var pattern = @"^### \[[ xX]\] " + System.Text.RegularExpressions.Regex.Escape(subtaskTitle) + @"\s*$";
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Multiline);

        var match = regex.Match(content);
        if (!match.Success) return;

        var updated = content[..match.Index] + $"### [{newCheck}] {subtaskTitle}" + content[(match.Index + match.Length)..];
        if (updated != content)
        {
            _selfWrites?.RegisterWrite(path);
            File.WriteAllText(path, updated);
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
        var path = GetFilePath(taskId);
        if (!File.Exists(path)) return;

        var content = File.ReadAllText(path);
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
        File.WriteAllText(path, rebuilt);
    }

    /// <summary>
    /// Save a task to disk. Creates or overwrites the file.
    /// </summary>
    public void Save(GlassworkTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new ArgumentException("Task must have an ID before saving.");

        var content = _parser.Serialize(task);
        var path = GetFilePath(task.Id);
        _selfWrites?.RegisterWrite(path);
        File.WriteAllText(path, content);
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

        File.WriteAllText(path, migrated);
        return true;
    }

    /// <summary>
    /// Delete a task file by ID.
    /// </summary>
    public bool Delete(string taskId)
    {
        var filePath = GetFilePath(taskId);
        if (!File.Exists(filePath)) return false;

        _selfWrites?.RegisterWrite(filePath);
        File.Delete(filePath);
        return true;
    }

    /// <summary>
    /// Check if a task file exists.
    /// </summary>
    public bool Exists(string taskId) => File.Exists(GetFilePath(taskId));

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

    private string GetFilePath(string taskId) => Path.Combine(_vaultPath, $"{taskId}.md");
}
