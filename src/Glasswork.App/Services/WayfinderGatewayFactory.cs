using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glasswork.Core.Research;

namespace Glasswork.Services;

internal static class WayfinderGatewayFactory
{
    private const string FixturePathVariable =
        "GLASSWORK_VISUAL_WAYFINDER_FIXTURE";

    public static IWayfinderGateway Create()
    {
        var fixturePath = Environment.GetEnvironmentVariable(FixturePathVariable);
        return string.IsNullOrWhiteSpace(fixturePath)
            ? new GhCliWayfinderGateway()
            : FixtureWayfinderGateway.Load(fixturePath);
    }

    private sealed class FixtureWayfinderGateway(
        Dictionary<string, FixtureIssue> issues) : IWayfinderGateway
    {
        private readonly Dictionary<string, FixtureIssue> _issues = issues;

        public static FixtureWayfinderGateway Load(string path)
        {
            var fixtures = JsonSerializer.Deserialize<List<FixtureIssue>>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    })
                ?? [];
            var issues = new Dictionary<string, FixtureIssue>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var fixture in fixtures)
            {
                if (WayfinderIssueIdentity.TryParse(
                        fixture.Reference,
                        out var identity))
                {
                    issues[identity.Canonical] = fixture;
                }
            }
            return new FixtureWayfinderGateway(issues);
        }

        public Task<WayfinderIssueLookup> GetIssueAsync(
            WayfinderIssueIdentity identity,
            string topicId,
            CancellationToken cancellationToken = default)
        {
            if (!_issues.TryGetValue(identity.Canonical, out var fixture))
            {
                return Task.FromResult(WayfinderIssueLookup.Unknown(
                    identity,
                    "No deterministic Wayfinder fixture was provided."));
            }
            return Task.FromResult(fixture.State switch
            {
                "open" => WayfinderIssueLookup.Available(
                    Snapshot(identity, fixture, WayfinderIssueStatus.Open)),
                "closed" => WayfinderIssueLookup.Available(
                    Snapshot(identity, fixture, WayfinderIssueStatus.Closed)),
                "inaccessible" => WayfinderIssueLookup.Inaccessible(
                    identity,
                    "GitHub issue status is inaccessible."),
                "not-found" => WayfinderIssueLookup.NotFound(identity),
                _ => WayfinderIssueLookup.Unknown(
                    identity,
                    "GitHub issue status is unknown."),
            });
        }

        public Task<WayfinderReciprocalResult> EnsureReciprocalReferenceAsync(
            WayfinderIssueIdentity identity,
            ResearchWayfinderTopicReference topic,
            CancellationToken cancellationToken = default)
        {
            if (!_issues.TryGetValue(identity.Canonical, out var fixture)
                || fixture.State is not ("open" or "closed"))
            {
                return Task.FromResult(WayfinderReciprocalResult.Failure(
                    "The fixture issue is not available."));
            }
            fixture.HasReciprocalReference = true;
            return Task.FromResult(WayfinderReciprocalResult.Added());
        }

        private static WayfinderIssueSnapshot Snapshot(
            WayfinderIssueIdentity identity,
            FixtureIssue fixture,
            WayfinderIssueStatus status) =>
            new(
                identity,
                string.IsNullOrWhiteSpace(fixture.Title)
                    ? identity.Canonical
                    : fixture.Title,
                status,
                fixture.HasReciprocalReference);
    }

    private sealed class FixtureIssue
    {
        public string Reference { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string State { get; set; } = "unknown";
        public bool HasReciprocalReference { get; set; }
    }
}
