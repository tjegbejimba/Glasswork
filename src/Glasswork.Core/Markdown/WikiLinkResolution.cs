namespace Glasswork.Core.Markdown;

/// <summary>
/// Resolution attached to a <see cref="WikiLinkSpan"/> at parse time. Decided
/// once and cached; the renderer/click handler never re-resolves.
/// </summary>
public abstract record WikiLinkResolution
{
    private WikiLinkResolution() { }

    /// <summary>Stem matches a known task id. Click navigates in-app.</summary>
    public sealed record Task(string TaskId) : WikiLinkResolution;

    /// <summary>Stem matches a non-task vault page. Click launches Obsidian.</summary>
    public sealed record VaultPage(string VaultRelativePath) : WikiLinkResolution;

    /// <summary>Stem matches nothing. Rendered muted, non-interactive.</summary>
    public sealed record Unresolved : WikiLinkResolution
    {
        public static readonly Unresolved Instance = new();
    }
}
