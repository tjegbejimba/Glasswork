namespace Glasswork.Core.Services;

/// <summary>
/// Classifies a parent string into one of three resolution types:
/// 1. InAppTask - matches a known Glasswork task id
/// 2. AdoUrl - resolves through AdoLinkResolver
/// 3. None - neither resolves
/// </summary>
public class ParentLinkClassifier
{
    private readonly IndexService _index;

    public ParentLinkClassifier(IndexService index)
    {
        _index = index;
    }

    public ParentLinkResolution Classify(string? parent, string? adoBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(parent))
            return ParentLinkResolution.None();

        var trimmed = parent.Trim();

        // Check if parent matches a known task id
        var task = _index.ById(trimmed);
        if (task is not null)
            return ParentLinkResolution.InAppTask(trimmed);

        // Try ADO resolution
        var adoUrl = AdoLinkResolver.TryResolve(trimmed, adoBaseUrl);
        if (adoUrl is not null)
            return ParentLinkResolution.AdoUrl(adoUrl);

        return ParentLinkResolution.None();
    }
}
