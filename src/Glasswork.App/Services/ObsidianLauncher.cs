using System;
using System.Threading.Tasks;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Windows.System;

namespace Glasswork.Services;

public sealed class ObsidianLauncher : IObsidianLauncher
{
    private readonly string _vaultRoot;
    private bool _notInstalledRaised;

    public ObsidianLauncher(string vaultRoot)
    {
        _vaultRoot = vaultRoot ?? throw new ArgumentNullException(nameof(vaultRoot));
    }

    public event EventHandler? NotInstalled;

    public async Task<bool> Open(string vaultRelativePath)
    {
        var uriString = ObsidianUriBuilder.ForVaultRelativePath(_vaultRoot, vaultRelativePath);
        if (uriString is null) return false;

        var launched = false;
        try
        {
            launched = await Launcher.LaunchUriAsync(new Uri(uriString));
        }
        catch
        {
            launched = false;
        }

        if (!launched && !_notInstalledRaised)
        {
            _notInstalledRaised = true;
            NotInstalled?.Invoke(this, EventArgs.Empty);
        }

        return launched;
    }
}
