namespace Glasswork.Core.Models;

/// <summary>
/// A parsed <c>glasswork://</c> deep-link URI representing a navigation target.
/// </summary>
public abstract record GlassworkUri
{
    private GlassworkUri() { }

    /// <summary>Navigate to the task detail view for the given task id.</summary>
    public sealed record Task(string TaskId) : GlassworkUri;

    /// <summary>Navigate to My Day.</summary>
    public sealed record MyDay : GlassworkUri;

    /// <summary>Navigate to Backlog.</summary>
    public sealed record Backlog : GlassworkUri;

    /// <summary>Navigate to the Research Topic library.</summary>
    public sealed record ResearchLibrary : GlassworkUri;

    /// <summary>Navigate to a selected Research Topic by stable Wiki Page id.</summary>
    public sealed record ResearchTopic(string TopicId) : GlassworkUri;
}
