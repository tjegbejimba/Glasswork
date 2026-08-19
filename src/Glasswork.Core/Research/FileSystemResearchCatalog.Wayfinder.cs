namespace Glasswork.Core.Research;

public sealed partial class FileSystemResearchCatalog
{
    public async Task<ResearchWayfinderResult> LinkExistingWayfinderAsync(
        string topicId,
        string issueReference,
        CancellationToken cancellationToken = default)
    {
        await _wayfinderMutationGate.WaitAsync(cancellationToken);
        try
        {
            return await LinkExistingWayfinderCoreAsync(
                topicId,
                issueReference,
                cancellationToken);
        }
        finally
        {
            _wayfinderMutationGate.Release();
        }
    }

    private async Task<ResearchWayfinderResult> LinkExistingWayfinderCoreAsync(
        string topicId,
        string issueReference,
        CancellationToken cancellationToken)
    {
        if (!WayfinderIssueIdentity.TryParse(issueReference, out var identity))
        {
            return ResearchWayfinderResult.Failure(
                ResearchWayfinderErrorCode.InvalidIdentity,
                "Enter a GitHub issue as owner/repository#number or a canonical GitHub issue URL.");
        }
        if (_wayfinderGateway is null)
        {
            return ResearchWayfinderResult.Failure(
                ResearchWayfinderErrorCode.ServicesUnavailable,
                "Wayfinder Related Work requires the GitHub owning-system gateway.");
        }

        lock (_gate)
        {
            if (!TryGetTopicCandidate(topicId, out var topic, out _))
            {
                return ResearchWayfinderResult.Failure(
                    ResearchWayfinderErrorCode.TopicNotFound,
                    $"No opted-in Research Topic with id '{topicId?.Trim()}' exists.");
            }
            if (topic.RelatedWayfinderReferences.Contains(
                    identity.Canonical,
                    StringComparer.OrdinalIgnoreCase))
            {
                return ResearchWayfinderResult.Failure(
                    ResearchWayfinderErrorCode.DuplicateRelationship,
                    $"Wayfinder issue '{identity.Canonical}' is already linked to '{topic.Title}'.");
            }
        }

        var lookup = await _wayfinderGateway.GetIssueAsync(
            identity,
            topicId,
            cancellationToken);
        if (lookup.State != WayfinderIssueLookupState.Available
            || lookup.Issue is null)
        {
            return LookupFailure(lookup);
        }

        WikiPageCandidate currentTopic;
        ResearchRelatedWorkResult write;
        lock (_gate)
        {
            if (!TryGetTopicCandidate(topicId, out currentTopic, out _))
            {
                return ResearchWayfinderResult.Failure(
                    ResearchWayfinderErrorCode.TopicNotFound,
                    $"No opted-in Research Topic with id '{topicId?.Trim()}' exists.");
            }
            if (currentTopic.RelatedWayfinderReferences.Contains(
                    identity.Canonical,
                    StringComparer.OrdinalIgnoreCase))
            {
                return ResearchWayfinderResult.Failure(
                    ResearchWayfinderErrorCode.DuplicateRelationship,
                    $"Wayfinder issue '{identity.Canonical}' is already linked to '{currentTopic.Title}'.");
            }
            write = WriteRelatedWayfinderReferences(
                currentTopic,
                identity.Canonical,
                included: true);
        }
        if (!write.Succeeded)
            return WriteFailure(write);

        var reciprocal = await _wayfinderGateway.EnsureReciprocalReferenceAsync(
            identity,
            new ResearchWayfinderTopicReference(
                currentTopic.Id,
                currentTopic.Title,
                currentTopic.VaultRelativePath),
            cancellationToken);
        var issue = lookup.Issue with
        {
            HasReciprocalReference =
                lookup.Issue.HasReciprocalReference || reciprocal.Succeeded,
        };

        lock (_gate)
        {
            _wayfinderByReference[identity.Canonical] =
                WayfinderProjectionState.Available(issue);
            var refreshed = RefreshRelatedWorkTopic(currentTopic.Id);
            var related = refreshed.RelatedWork.ActiveWayfinder
                .Concat(refreshed.RelatedWork.CompletedWayfinder)
                .Single(item => string.Equals(
                    item.Identity.Canonical,
                    identity.Canonical,
                    StringComparison.OrdinalIgnoreCase));
            var message = reciprocal.Succeeded
                ? $"Linked Wayfinder issue '{identity.Canonical}' to '{refreshed.Title}' with a reciprocal reference."
                : $"Linked Wayfinder issue '{identity.Canonical}', but GitHub could not add the reciprocal reference: {reciprocal.Message}";
            return ResearchWayfinderResult.Success(refreshed, related, message);
        }
    }

    public async Task<ResearchWayfinderResult> RepairRelatedWayfinderAsync(
        string topicId,
        string issueReference,
        CancellationToken cancellationToken = default)
    {
        await _wayfinderMutationGate.WaitAsync(cancellationToken);
        try
        {
            return await RepairRelatedWayfinderCoreAsync(
                topicId,
                issueReference,
                cancellationToken);
        }
        finally
        {
            _wayfinderMutationGate.Release();
        }
    }

    private async Task<ResearchWayfinderResult> RepairRelatedWayfinderCoreAsync(
        string topicId,
        string issueReference,
        CancellationToken cancellationToken)
    {
        if (!WayfinderIssueIdentity.TryParse(issueReference, out var identity))
        {
            return ResearchWayfinderResult.Failure(
                ResearchWayfinderErrorCode.InvalidIdentity,
                "The Wayfinder issue identity is malformed.");
        }
        if (_wayfinderGateway is null)
        {
            return ResearchWayfinderResult.Failure(
                ResearchWayfinderErrorCode.ServicesUnavailable,
                "Wayfinder Related Work requires the GitHub owning-system gateway.");
        }

        WikiPageCandidate topic;
        lock (_gate)
        {
            if (!TryGetTopicCandidate(topicId, out topic, out _))
            {
                return ResearchWayfinderResult.Failure(
                    ResearchWayfinderErrorCode.TopicNotFound,
                    $"No opted-in Research Topic with id '{topicId?.Trim()}' exists.");
            }
        }

        var lookup = await _wayfinderGateway.GetIssueAsync(
            identity,
            topic.Id,
            cancellationToken);
        if (lookup.State == WayfinderIssueLookupState.NotFound)
        {
            ResearchRelatedWorkResult write;
            lock (_gate)
            {
                write = WriteRelatedWayfinderReferences(
                    topic,
                    identity.Canonical,
                    included: false);
                if (write.Succeeded)
                {
                    _wayfinderByReference.Remove(identity.Canonical);
                    var refreshed = RefreshRelatedWorkTopic(topic.Id);
                    return ResearchWayfinderResult.Success(
                        refreshed,
                        new ResearchRelatedWayfinder(
                            identity,
                            identity.Canonical,
                            WayfinderIssueStatus.Unknown,
                            WayfinderRelationState.BrokenReference),
                        $"Removed broken Wayfinder reference '{identity.Canonical}' from '{topic.Title}'.");
                }
            }
            return WriteFailure(write);
        }
        if (lookup.State != WayfinderIssueLookupState.Available
            || lookup.Issue is null)
        {
            return LookupFailure(lookup);
        }

        ResearchRelatedWorkResult canonicalWrite;
        lock (_gate)
        {
            canonicalWrite = WriteRelatedWayfinderReferences(
                topic,
                identity.Canonical,
                included: true);
        }
        if (!canonicalWrite.Succeeded)
            return WriteFailure(canonicalWrite);

        var reciprocal = await _wayfinderGateway.EnsureReciprocalReferenceAsync(
            identity,
            new ResearchWayfinderTopicReference(
                topic.Id,
                topic.Title,
                topic.VaultRelativePath),
            cancellationToken);
        if (!reciprocal.Succeeded)
        {
            return ResearchWayfinderResult.Failure(
                ResearchWayfinderErrorCode.Inaccessible,
                reciprocal.Message);
        }

        lock (_gate)
        {
            _wayfinderByReference[identity.Canonical] =
                WayfinderProjectionState.Available(
                    lookup.Issue with { HasReciprocalReference = true });
            var refreshed = RefreshRelatedWorkTopic(topic.Id);
            var related = refreshed.RelatedWork.ActiveWayfinder
                .Concat(refreshed.RelatedWork.CompletedWayfinder)
                .Single(item => string.Equals(
                    item.Identity.Canonical,
                    identity.Canonical,
                    StringComparison.OrdinalIgnoreCase));
            return ResearchWayfinderResult.Success(
                refreshed,
                related,
                $"Repaired Wayfinder issue '{identity.Canonical}' and Research Topic '{topic.Title}'.");
        }
    }

    public async Task<ResearchWayfinderRefreshResult> RefreshWayfinderAsync(
        string topicId,
        CancellationToken cancellationToken = default)
    {
        if (_wayfinderGateway is null)
            return ResearchWayfinderRefreshResult.Failure(
                "Wayfinder status is unavailable because the GitHub owning-system gateway is not configured.");

        WikiPageCandidate topic;
        lock (_gate)
        {
            if (!TryGetTopicCandidate(topicId, out topic, out _))
            {
                return ResearchWayfinderRefreshResult.Failure(
                    $"No opted-in Research Topic with id '{topicId?.Trim()}' exists.");
            }
        }

        foreach (var reference in topic.RelatedWayfinderReferences)
        {
            if (!WayfinderIssueIdentity.TryParse(reference, out var identity))
                continue;
            var lookup = await _wayfinderGateway.GetIssueAsync(
                identity,
                topic.Id,
                cancellationToken);
            lock (_gate)
                _wayfinderByReference[identity.Canonical] =
                    WayfinderProjectionState.FromLookup(lookup);
        }

        lock (_gate)
            return ResearchWayfinderRefreshResult.Success(
                RefreshRelatedWorkTopic(topic.Id));
    }

    private RelatedWayfinderProjection BuildRelatedWayfinder(
        WikiPageCandidate topic,
        ICollection<ResearchRelatedWorkWarning> warnings)
    {
        var related = new List<ResearchRelatedWayfinder>();
        foreach (var reference in topic.RelatedWayfinderReferences)
        {
            if (!WayfinderIssueIdentity.TryParse(reference, out var identity))
                continue;
            _wayfinderByReference.TryGetValue(
                identity.Canonical,
                out var projection);
            projection ??= WayfinderProjectionState.Unknown(
                identity,
                "Owning-system status has not been refreshed.");
            var item = projection.ToRelated();
            related.Add(item);
            if (item.RelationState == WayfinderRelationState.MissingReciprocalReference)
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    identity.Canonical,
                    ResearchRelatedWorkWarningCode.MissingWayfinderReciprocalReference,
                    $"Wayfinder issue '{identity.Canonical}' is missing its reciprocal Research Topic reference. Repair restores it.",
                    CanRepair: true));
            }
            else if (item.RelationState == WayfinderRelationState.BrokenReference)
            {
                warnings.Add(new ResearchRelatedWorkWarning(
                    identity.Canonical,
                    ResearchRelatedWorkWarningCode.BrokenWayfinderReference,
                    $"Wayfinder issue '{identity.Canonical}' no longer exists. Repair removes the stale Topic reference.",
                    CanRepair: true));
            }
        }

        var active = related
            .Where(item => item.Status != WayfinderIssueStatus.Closed)
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity.Canonical, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var completed = related
            .Where(item => item.Status == WayfinderIssueStatus.Closed)
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity.Canonical, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new RelatedWayfinderProjection(
            Array.AsReadOnly(active),
            Array.AsReadOnly(completed));
    }

    private static ResearchWayfinderResult LookupFailure(WayfinderIssueLookup lookup) =>
        lookup.State switch
        {
            WayfinderIssueLookupState.NotFound =>
                ResearchWayfinderResult.Failure(
                    ResearchWayfinderErrorCode.IssueNotFound,
                    lookup.Message),
            WayfinderIssueLookupState.Inaccessible =>
                ResearchWayfinderResult.Failure(
                    ResearchWayfinderErrorCode.Inaccessible,
                    lookup.Message),
            _ => ResearchWayfinderResult.Failure(
                ResearchWayfinderErrorCode.UnknownStatus,
                lookup.Message),
        };

    private static ResearchWayfinderResult WriteFailure(
        ResearchRelatedWorkResult write) =>
        ResearchWayfinderResult.Failure(
            write.ErrorCode switch
            {
                ResearchRelatedWorkErrorCode.ConcurrentModification =>
                    ResearchWayfinderErrorCode.ConcurrentModification,
                ResearchRelatedWorkErrorCode.InvalidResearchMetadata =>
                    ResearchWayfinderErrorCode.InvalidResearchMetadata,
                _ => ResearchWayfinderErrorCode.WriteFailed,
            },
            write.Message);

    private sealed record RelatedWayfinderProjection(
        IReadOnlyList<ResearchRelatedWayfinder> Active,
        IReadOnlyList<ResearchRelatedWayfinder> Completed);

    private sealed record WayfinderProjectionState(
        WayfinderIssueIdentity Identity,
        string Title,
        WayfinderIssueStatus Status,
        WayfinderRelationState RelationState)
    {
        public static WayfinderProjectionState Available(
            WayfinderIssueSnapshot issue) =>
            new(
                issue.Identity,
                issue.Title,
                issue.Status,
                issue.HasReciprocalReference
                    ? WayfinderRelationState.Healthy
                    : WayfinderRelationState.MissingReciprocalReference);

        public static WayfinderProjectionState FromLookup(
            WayfinderIssueLookup lookup) =>
            lookup.State switch
            {
                WayfinderIssueLookupState.Available when lookup.Issue is not null =>
                    Available(lookup.Issue),
                WayfinderIssueLookupState.NotFound =>
                    new(
                        lookup.Identity,
                        $"Missing Wayfinder issue: {lookup.Identity.Canonical}",
                        WayfinderIssueStatus.Unknown,
                        WayfinderRelationState.BrokenReference),
                WayfinderIssueLookupState.Inaccessible =>
                    new(
                        lookup.Identity,
                        lookup.Identity.Canonical,
                        WayfinderIssueStatus.Inaccessible,
                        WayfinderRelationState.Unknown),
                _ => Unknown(lookup.Identity, lookup.Message),
            };

        public static WayfinderProjectionState Unknown(
            WayfinderIssueIdentity identity,
            string message) =>
            new(
                identity,
                identity.Canonical,
                WayfinderIssueStatus.Unknown,
                WayfinderRelationState.Unknown);

        public ResearchRelatedWayfinder ToRelated() =>
            new(Identity, Title, Status, RelationState);
    }
}
