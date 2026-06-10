namespace Glasswork.Core.Services;

/// <summary>
/// Represents the resolved type of a parent link.
/// </summary>
public class ParentLinkResolution
{
    public enum ResolutionType
    {
        InAppTask,
        AdoUrl,
        None
    }

    public ResolutionType Type { get; init; }
    public string? TaskId { get; init; }
    public string? Url { get; init; }

    public static ParentLinkResolution InAppTask(string taskId) => new()
    {
        Type = ResolutionType.InAppTask,
        TaskId = taskId
    };

    public static ParentLinkResolution AdoUrl(string url) => new()
    {
        Type = ResolutionType.AdoUrl,
        Url = url
    };

    public static ParentLinkResolution None() => new()
    {
        Type = ResolutionType.None
    };
}
