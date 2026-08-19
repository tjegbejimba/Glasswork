namespace Glasswork.Core.Research;

public interface IResearchChangeLogStore
{
    ResearchChangeLogAppendResult Append(
        string topicId,
        string summary,
        IReadOnlyCollection<string> changedPageIds);

    ResearchChangeLog Read(string topicId);
}

public sealed record ResearchChangeLog(
    string TopicId,
    ResearchChangeLogState State,
    IReadOnlyList<ResearchChangeLogEntry> Entries,
    string Markdown,
    string VaultRelativePath,
    string FullPath,
    string? Message)
{
    public string DisplayMarkdown { get; init; } = string.Empty;
    public string? Revision { get; init; }

    public static ResearchChangeLog Missing(string topicId) =>
        new(
            topicId,
            ResearchChangeLogState.Missing,
            Array.Empty<ResearchChangeLogEntry>(),
            string.Empty,
            $"wiki/research-logs/{topicId}.md",
            string.Empty,
            "No Research history has been recorded yet.");
}

public sealed record ResearchChangeLogEntry(
    DateTimeOffset Timestamp,
    string Summary,
    IReadOnlyList<string> ChangedPageIds);

public sealed record ResearchChangeLogAppendResult(
    ResearchChangeLogAppendStatus Status,
    ResearchChangeLog Log,
    string Message);

public enum ResearchChangeLogState
{
    Missing,
    Empty,
    Available,
    Malformed,
}

public enum ResearchChangeLogAppendStatus
{
    Appended,
    NoKnowledgeChanges,
    InvalidRequest,
    MalformedLog,
    ConcurrentModification,
    WriteFailed,
}
