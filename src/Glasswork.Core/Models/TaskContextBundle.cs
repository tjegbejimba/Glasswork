using System.Collections.Generic;

namespace Glasswork.Core.Models;

/// <summary>
/// A compact handoff packet for one Task: Description, Notes, active Subtasks,
/// Links, latest Artifacts, Backlinks, open blockers, and relevant Vault paths.
/// Designed for agent handoff — includes enough context for a Copilot-style agent
/// to resume work without re-discovering the Task manually.
/// </summary>
public sealed record TaskContextBundle(
    string TaskId,
    string Title,
    string Status,
    string? Description,
    string? Notes,
    List<SubTask> ActiveSubtasks,
    List<TaskLink> Links,
    List<Artifact> LatestArtifacts,
    List<Backlink> Backlinks,
    List<SubTask> OpenBlockers,
    string TaskFilePath,
    string? ArtifactsPath,
    string? Size,
    string ResourceRevision);
