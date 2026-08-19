using System.Text.RegularExpressions;

namespace Glasswork.Core.Research;

public interface IWayfinderGateway
{
    Task<WayfinderIssueLookup> GetIssueAsync(
        WayfinderIssueIdentity identity,
        string topicId,
        CancellationToken cancellationToken = default);

    Task<WayfinderReciprocalResult> EnsureReciprocalReferenceAsync(
        WayfinderIssueIdentity identity,
        ResearchWayfinderTopicReference topic,
        CancellationToken cancellationToken = default);
}

public sealed partial record WayfinderIssueIdentity(
    string Owner,
    string Repository,
    int IssueNumber)
{
    public string Canonical => $"{Owner}/{Repository}#{IssueNumber}";
    public Uri Uri => new(
        $"https://github.com/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repository)}/issues/{IssueNumber}");

    public static bool TryParse(
        string? value,
        out WayfinderIssueIdentity identity)
    {
        identity = null!;
        var input = value?.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return false;
        var match = CanonicalRegex().Match(input);
        if (!match.Success)
            match = GitHubIssueUrlRegex().Match(input);
        if (!match.Success
            || !int.TryParse(match.Groups["number"].Value, out var number)
            || number <= 0)
        {
            return false;
        }

        identity = new WayfinderIssueIdentity(
            match.Groups["owner"].Value,
            match.Groups["repo"].Value,
            number);
        return true;
    }

    [GeneratedRegex(
        @"\A(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]+)#(?<number>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalRegex();

    [GeneratedRegex(
        @"\Ahttps://github\.com/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]+)/issues/(?<number>[1-9][0-9]*)(?:[/?#].*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubIssueUrlRegex();
}

public sealed record WayfinderIssueSnapshot(
    WayfinderIssueIdentity Identity,
    string Title,
    WayfinderIssueStatus Status,
    bool HasReciprocalReference);

public enum WayfinderIssueStatus
{
    Open,
    Closed,
    Unknown,
    Inaccessible,
}

public sealed record WayfinderIssueLookup(
    WayfinderIssueLookupState State,
    WayfinderIssueIdentity Identity,
    WayfinderIssueSnapshot? Issue,
    string Message)
{
    public static WayfinderIssueLookup Available(WayfinderIssueSnapshot issue) =>
        new(
            WayfinderIssueLookupState.Available,
            issue.Identity,
            issue,
            "Wayfinder issue is available.");

    public static WayfinderIssueLookup NotFound(WayfinderIssueIdentity identity) =>
        new(
            WayfinderIssueLookupState.NotFound,
            identity,
            null,
            $"GitHub issue '{identity.Canonical}' was not found.");

    public static WayfinderIssueLookup Inaccessible(
        WayfinderIssueIdentity identity,
        string message) =>
        new(WayfinderIssueLookupState.Inaccessible, identity, null, message);

    public static WayfinderIssueLookup Unknown(
        WayfinderIssueIdentity identity,
        string message) =>
        new(WayfinderIssueLookupState.Unknown, identity, null, message);
}

public enum WayfinderIssueLookupState
{
    Available,
    NotFound,
    Inaccessible,
    Unknown,
}

public sealed record ResearchWayfinderTopicReference(
    string TopicId,
    string TopicTitle,
    string VaultRelativePath)
{
    public string DeepLink =>
        $"glasswork://research/{Uri.EscapeDataString(TopicId)}";
}

public sealed record WayfinderReciprocalResult(
    bool Succeeded,
    bool AddedReference,
    string Message)
{
    public static WayfinderReciprocalResult Added() =>
        new(true, true, "Added reciprocal Research Topic reference.");

    public static WayfinderReciprocalResult AlreadyPresent() =>
        new(true, false, "Reciprocal Research Topic reference already exists.");

    public static WayfinderReciprocalResult Failure(string message) =>
        new(false, false, message);
}
