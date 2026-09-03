using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Glasswork.Core.Services;

/// <summary>One persisted Session Task Set member: a Task ID and its last-known title.</summary>
public sealed record SessionTaskSetMemberState(string TaskId, string Title);

/// <summary>
/// Result of <see cref="SessionTaskSetStateStore.Load"/>. A session that was
/// never saved is a legitimate empty Session Task Set (<see cref="Ok"/> true,
/// <see cref="Members"/> empty). A failed load means the persisted value
/// exists but could not be trusted — callers must surface
/// <see cref="ErrorCode"/>/<see cref="ErrorMessage"/> visibly rather than
/// treating it as an empty set.
/// </summary>
public sealed record SessionTaskSetLoadResult(bool Ok, string? ErrorCode, string? ErrorMessage, IReadOnlyList<SessionTaskSetMemberState> Members)
{
    public static SessionTaskSetLoadResult Empty { get; } = new(true, null, null, []);
}

/// <summary>
/// Persists the Session Task Set (ordered Task IDs and last-known titles) for
/// one Copilot session, keyed by the stable Copilot session ID, through the
/// existing cross-process-safe <see cref="IUiStateService"/>. See ADR 0001,
/// ADR 0026, and UBIQUITOUS_LANGUAGE.md "Session Task Set".
///
/// Persists only Task ID and last-known title per member — never Description,
/// Notes, operational metadata, Artifacts, or relationship content. Selection
/// and reading position (scroll/expander state) are intentionally never
/// persisted: restoration always selects the most-recent member (the first
/// entry in recency order) and resets reading position.
///
/// Because each session's state lives under its own key
/// (<c>sessionTaskSet.&lt;sessionId&gt;</c>), concurrent canvas hosts for
/// different Copilot sessions never read, select, or mutate each other's
/// membership, and <see cref="JsonFileUiStateService"/>'s merge-on-save
/// behavior means one host's save cannot clobber another's key.
/// </summary>
public sealed class SessionTaskSetStateStore(IUiStateService uiState)
{
    public const string KeyPrefix = "sessionTaskSet.";
    private const int CurrentVersion = 1;

    private static string KeyFor(string sessionId) => KeyPrefix + sessionId;

    /// <summary>
    /// Loads the persisted Session Task Set for <paramref name="sessionId"/>.
    /// Malformed or future-version persisted state fails visibly (<see
    /// cref="SessionTaskSetLoadResult.Ok"/> false) rather than silently
    /// producing a success-shaped empty set; only a session with no persisted
    /// key at all is treated as legitimately empty.
    /// </summary>
    public SessionTaskSetLoadResult Load(string sessionId)
    {
        // Get&lt;JsonElement&gt; never throws on shape mismatch (JsonElement
        // always deserializes), so we can distinguish "never saved" (default,
        // JsonValueKind.Undefined) from "saved but malformed" (validated by
        // hand below) instead of IUiStateService.Get&lt;T&gt;'s generic
        // catch-and-default behavior collapsing both cases into the same
        // silent empty result.
        var element = uiState.Get<JsonElement>(KeyFor(sessionId));
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return SessionTaskSetLoadResult.Empty;

        try
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number)
            {
                return Failure(sessionId, "malformed_state", "the persisted Session Task Set has no readable version.");
            }

            var version = versionElement.GetInt32();
            if (version != CurrentVersion)
            {
                return Failure(sessionId, "unsupported_version", $"the persisted Session Task Set is version {version}, which this canvas host cannot read.");
            }

            if (!element.TryGetProperty("members", out var membersElement) || membersElement.ValueKind != JsonValueKind.Array)
            {
                return Failure(sessionId, "malformed_state", "the persisted Session Task Set has no members array.");
            }

            var members = new List<SessionTaskSetMemberState>();
            foreach (var member in membersElement.EnumerateArray())
            {
                if (member.ValueKind != JsonValueKind.Object ||
                    !member.TryGetProperty("taskId", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    return Failure(sessionId, "malformed_state", "the persisted Session Task Set contains a member with no valid taskId.");
                }

                var title = member.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString() ?? string.Empty
                    : string.Empty;
                members.Add(new SessionTaskSetMemberState(idElement.GetString()!, title));
            }

            return new SessionTaskSetLoadResult(true, null, null, members);
        }
        catch (Exception ex)
        {
            return Failure(sessionId, "malformed_state", $"the persisted Session Task Set could not be read ({ex.Message}).");
        }
    }

    private static SessionTaskSetLoadResult Failure(string sessionId, string code, string reason) =>
        new(false, code, $"Session '{sessionId}': {reason}", []);

    /// <summary>
    /// Persists <paramref name="members"/> as the current membership and
    /// recency order (index 0 = most recent) for <paramref name="sessionId"/>
    /// and flushes immediately, so a killed host process never loses the last
    /// explicit change. An empty list removes the persisted key entirely —
    /// clearing a Session Task Set is indistinguishable from one that was
    /// never populated, and also self-heals a previously malformed entry.
    /// </summary>
    public void Save(string sessionId, IReadOnlyList<SessionTaskSetMemberState> members)
    {
        var key = KeyFor(sessionId);
        if (members.Count == 0)
        {
            uiState.Remove(key);
        }
        else
        {
            uiState.Set(key, new
            {
                version = CurrentVersion,
                members = members.Select(m => new { taskId = m.TaskId, title = m.Title }).ToList(),
            });
        }
        uiState.Save();
    }
}
