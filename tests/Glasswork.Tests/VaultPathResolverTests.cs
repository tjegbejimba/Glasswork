using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultPathResolverTests
{
    [TestMethod]
    public void Resolve_AcceptsVaultRootAndLegacyTaskDirectory()
    {
        var vaultRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "glasswork-vault"));
        var taskDirectory = Path.Combine(vaultRoot, "wiki", "todo");

        var fromRoot = VaultPathResolver.Resolve(vaultRoot);
        var fromTaskDirectory = VaultPathResolver.Resolve(taskDirectory);

        Assert.AreEqual(vaultRoot, fromRoot.VaultRoot);
        Assert.AreEqual(taskDirectory, fromRoot.TaskDirectory);
        Assert.AreEqual(vaultRoot, fromTaskDirectory.VaultRoot);
        Assert.AreEqual(taskDirectory, fromTaskDirectory.TaskDirectory);
    }
}
