using Glasswork.Core.Models;

namespace Glasswork.Core.Research;

public static class WayfinderNavigationPolicy
{
    public static Uri? Resolve(WayfinderIssueIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var uri = identity.Uri;
        return ArtifactLinkPolicy.Decide(uri.AbsoluteUri)
            == ArtifactLinkPolicy.Decision.Allow
                ? uri
                : null;
    }
}
