using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Completed startup Task-readiness stage. <see cref="Index"/> is fully seeded,
/// subscribed to same-process Vault mutations, and ready for normal navigation.
/// </summary>
/// <param name="Index">The ready in-memory Task aggregate.</param>
/// <param name="TaskCount">Distinct parsed Tasks published to the Index.</param>
/// <param name="MigratedTaskCount">Task files successfully migrated to V2.</param>
/// <param name="ExaminedFileCount">
/// Top-level non-underscore <c>*.md</c> candidates attempted. Malformed or
/// unreadable candidates count as examined but not as Tasks.
/// </param>
public sealed record IndexStartupHydrationResult(
    IndexService Index,
    int TaskCount,
    int MigratedTaskCount,
    int ExaminedFileCount);

internal sealed record VaultStartupSnapshot(
    IReadOnlyList<GlassworkTask> Tasks,
    int MigratedTaskCount,
    int ExaminedFileCount);
