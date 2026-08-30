namespace Glasswork.Core.Models;

public enum ChildActivitySummaryStateKind
{
    Missing,
    Current,
    OutOfDate,
    Failed,
}

public sealed record ChildActivitySummaryArtifactInput(
    string Filename,
    ArtifactKind Kind,
    long SizeBytes,
    string ResourceRevision,
    string? Content,
    bool IsDescendantSummary);

public sealed record ChildActivitySummaryTaskInput(
    string Id,
    string Title,
    string Status,
    string Type,
    string? SourceKind,
    string ResourceRevision,
    string Notes,
    IReadOnlyList<TaskLink> Links,
    IReadOnlyList<ChildActivitySummaryArtifactInput> Artifacts);

public sealed record ChildActivitySummaryGroup(
    ChildActivitySummaryTaskInput DirectChild,
    IReadOnlyList<ChildActivitySummaryTaskInput> Tasks);

public sealed record ChildActivitySummaryCapture(
    string ParentId,
    string ParentRevision,
    int DescendantCount,
    IReadOnlyList<ChildActivitySummaryGroup> Groups,
    IReadOnlyDictionary<string, string> ReadBasis,
    string? ExpectedSummaryRevision);

public sealed record ChildActivitySummaryState(
    ChildActivitySummaryStateKind Kind,
    string? Body = null,
    DateTimeOffset? GeneratedAt = null,
    int DescendantCount = 0,
    IReadOnlyDictionary<string, string>? ReadBasis = null,
    string? ResourceRevision = null,
    string? Error = null);

public sealed class ChildActivitySummaryException : Exception
{
    public ChildActivitySummaryException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
