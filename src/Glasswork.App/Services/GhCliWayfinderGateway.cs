using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glasswork.Core.Research;

namespace Glasswork.Services;

public sealed class GhCliWayfinderGateway : IWayfinderGateway
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<WayfinderIssueLookup> GetIssueAsync(
        WayfinderIssueIdentity identity,
        string topicId,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            [
                "issue",
                "view",
                identity.IssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--repo",
                $"{identity.Owner}/{identity.Repository}",
                "--json",
                "title,state,comments",
            ],
            cancellationToken);
        if (!result.Succeeded)
        {
            if (result.Error.Contains("Could not resolve to an issue", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return WayfinderIssueLookup.NotFound(identity);
            }
            if (result.Error.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("auth login", StringComparison.OrdinalIgnoreCase)
                || result.Error.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
            {
                return WayfinderIssueLookup.Inaccessible(
                    identity,
                    $"GitHub issue '{identity.Canonical}' is inaccessible. Sign in with `gh auth login` or verify repository access.");
            }
            return WayfinderIssueLookup.Unknown(
                identity,
                $"GitHub could not refresh '{identity.Canonical}': {result.Error}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            var title = root.GetProperty("title").GetString()?.Trim();
            var state = root.GetProperty("state").GetString();
            var marker = ReciprocalMarker(topicId);
            var hasReciprocal = root.TryGetProperty("comments", out var comments)
                && comments.EnumerateArray().Any(comment =>
                    comment.TryGetProperty("body", out var body)
                    && body.GetString()?.Contains(
                        marker,
                        StringComparison.Ordinal) == true);
            return WayfinderIssueLookup.Available(new WayfinderIssueSnapshot(
                identity,
                string.IsNullOrWhiteSpace(title) ? identity.Canonical : title,
                string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase)
                    ? WayfinderIssueStatus.Closed
                    : WayfinderIssueStatus.Open,
                hasReciprocal));
        }
        catch (JsonException ex)
        {
            return WayfinderIssueLookup.Unknown(
                identity,
                $"GitHub returned unreadable status for '{identity.Canonical}': {ex.Message}");
        }
    }

    public async Task<WayfinderReciprocalResult> EnsureReciprocalReferenceAsync(
        WayfinderIssueIdentity identity,
        ResearchWayfinderTopicReference topic,
        CancellationToken cancellationToken = default)
    {
        var lookup = await GetIssueAsync(identity, topic.TopicId, cancellationToken);
        if (lookup.State != WayfinderIssueLookupState.Available
            || lookup.Issue is null)
        {
            return WayfinderReciprocalResult.Failure(lookup.Message);
        }
        if (lookup.Issue.HasReciprocalReference)
            return WayfinderReciprocalResult.AlreadyPresent();

        var marker = ReciprocalMarker(topic.TopicId);
        var body =
            $"""
             Related Research Topic: **{topic.TopicTitle}**

             Open in Glasswork: `{topic.DeepLink}`
             Vault page: `{topic.VaultRelativePath}`

             {marker}
             """;
        var result = await RunAsync(
            [
                "issue",
                "comment",
                identity.IssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--repo",
                $"{identity.Owner}/{identity.Repository}",
                "--body",
                body,
            ],
            cancellationToken);
        return result.Succeeded
            ? WayfinderReciprocalResult.Added()
            : WayfinderReciprocalResult.Failure(
                $"GitHub could not add the reciprocal Research Topic reference: {result.Error}");
    }

    internal static string ReciprocalMarker(string topicId) =>
        $"<!-- glasswork-research-topic:{topicId.Trim()} -->";

    private static async Task<GhResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = GhCliIssueFiler.ResolveGhPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return GhResult.Failure(
                $"GitHub CLI is unavailable: {ex.Message}");
        }
        if (process is null)
            return GhResult.Failure("GitHub CLI could not be launched.");

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken))
        {
            timeout.CancelAfter(Timeout);
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = (await outputTask).Trim();
                var error = (await errorTask).Trim();
                return process.ExitCode == 0
                    ? GhResult.Success(output)
                    : GhResult.Failure(
                        string.IsNullOrWhiteSpace(error)
                            ? $"gh exited with code {process.ExitCode}."
                            : error);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process cleanup is best effort after timeout.
                }
                return GhResult.Failure("GitHub CLI timed out.");
            }
        }
    }

    private sealed record GhResult(
        bool Succeeded,
        string Output,
        string Error)
    {
        public static GhResult Success(string output) =>
            new(true, output, string.Empty);

        public static GhResult Failure(string error) =>
            new(false, string.Empty, error);
    }
}
