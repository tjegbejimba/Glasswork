using Glasswork.Core.AppUpdate;
using Glasswork.Core.Services;

namespace Glasswork.App.Services;

/// <summary>
/// Reads the Glasswork source repository path from UI State.
/// Returns null if not yet configured (e.g., first launch before publish.ps1 stamps it).
/// </summary>
internal sealed class UiStateRepoPathProvider : IRepoPathProvider
{
    private readonly IUiStateService _uiState;
    private readonly string _repoPathKey;

    public UiStateRepoPathProvider(IUiStateService uiState, string repoPathKey)
    {
        _uiState = uiState ?? throw new ArgumentNullException(nameof(uiState));
        _repoPathKey = repoPathKey ?? throw new ArgumentNullException(nameof(repoPathKey));
    }

    public string? GetRepoPath()
    {
        return _uiState.Get(_repoPathKey);
    }
}
