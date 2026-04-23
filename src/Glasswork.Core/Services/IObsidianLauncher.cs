using System;
using System.Threading.Tasks;

namespace Glasswork.Core.Services;

public interface IObsidianLauncher
{
    event EventHandler? NotInstalled;

    Task<bool> Open(string vaultRelativePath);
}
