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

    public VaultService(string vaultPath)
    {
        _vaultPath = vaultPath;
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
    /// Save a task to disk. Creates or overwrites the file.
    /// </summary>
    public void Save(GlassworkTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new ArgumentException("Task must have an ID before saving.");

        var content = _parser.Serialize(task);
        File.WriteAllText(GetFilePath(task.Id), content);
    }

    /// <summary>
    /// Delete a task file by ID.
    /// </summary>
    public bool Delete(string taskId)
    {
        var filePath = GetFilePath(taskId);
        if (!File.Exists(filePath)) return false;

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
