using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Builds Task context bundles for agent handoff. Assembles a compact packet
/// containing all relevant context for resuming work on a task.
/// </summary>
public class TaskContextService
{
    private readonly VaultService _vault;
    private readonly IArtifactStore? _artifactStore;
    private readonly IBacklinkIndex? _backlinkIndex;

    public TaskContextService(VaultService vault, IArtifactStore? artifactStore = null, IBacklinkIndex? backlinkIndex = null)
    {
        _vault = vault;
        _artifactStore = artifactStore;
        _backlinkIndex = backlinkIndex;
    }

    /// <summary>
    /// Builds a context bundle for the specified task.
    /// Returns null if the task does not exist.
    /// </summary>
    public TaskContextBundle? BuildContextBundle(string taskId)
    {
        var task = _vault.Load(taskId);
        if (task == null)
            return null;

        var taskFilePath = Path.Combine(_vault.VaultPath, $"{taskId}.md");

        // Active subtasks: todo, in_progress, blocked (not done or dropped)
        var activeSubtasks = task.Subtasks
            .Where(s => s.Status != "done" && s.Status != "dropped")
            .ToList();

        // Open blockers: subtasks in blocked status
        var openBlockers = task.Subtasks
            .Where(s => s.Status == "blocked")
            .ToList();

        // Latest artifacts (sorted by mtime descending)
        var artifacts = _artifactStore?.Load(taskId) ?? new List<Artifact>();

        // Backlinks
        var backlinks = _backlinkIndex?.GetBacklinks(taskId) ?? new List<Backlink>();

        // Artifacts path
        var artifactsPath = Path.Combine(_vault.VaultPath, $"{taskId}.artifacts");
        var hasArtifactsDir = Directory.Exists(artifactsPath);

        return new TaskContextBundle(
            TaskId: task.Id,
            Title: task.Title,
            Status: task.Status,
            Description: task.Description,
            Notes: task.Notes,
            ActiveSubtasks: activeSubtasks,
            Links: task.Links,
            LatestArtifacts: artifacts.ToList(),
            Backlinks: backlinks.ToList(),
            OpenBlockers: openBlockers,
            TaskFilePath: taskFilePath,
            ArtifactsPath: hasArtifactsDir ? artifactsPath : null);
    }
}
