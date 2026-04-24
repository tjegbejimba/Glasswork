using Glasswork.Mcp;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class VaultPathGuardTests
{
    private static string NewVaultDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tests", Guid.NewGuid().ToString("N"), "vault");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- Accepted paths ---

    [TestMethod]
    public void VaultRelativePath_IsAccepted()
    {
        var vault = NewVaultDir();
        var result = VaultPathGuard.EnsurePathInVault(vault, "tasks/my-task.md");
        Assert.AreEqual(Path.GetFullPath(Path.Combine(vault, "tasks/my-task.md")), result);
    }

    [TestMethod]
    public void VaultRelativePath_NestedDeep_IsAccepted()
    {
        var vault = NewVaultDir();
        var result = VaultPathGuard.EnsurePathInVault(vault, "a/b/c/d.md");
        Assert.AreEqual(Path.GetFullPath(Path.Combine(vault, "a/b/c/d.md")), result);
    }

    [TestMethod]
    public void AbsolutePathInsideVault_IsAccepted()
    {
        var vault = NewVaultDir();
        var insidePath = Path.Combine(vault, "tasks", "my-task.md");
        var result = VaultPathGuard.EnsurePathInVault(vault, insidePath);
        Assert.AreEqual(Path.GetFullPath(insidePath), result);
    }

    [TestMethod]
    public void VaultRootItself_IsAccepted()
    {
        var vault = NewVaultDir();
        var result = VaultPathGuard.EnsurePathInVault(vault, vault);
        Assert.AreEqual(Path.GetFullPath(vault), result);
    }

    // --- Rejected paths ---

    [TestMethod]
    public void DotDotTraversal_IsRejected()
    {
        var vault = NewVaultDir();
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => VaultPathGuard.EnsurePathInVault(vault, "../escape.md"));
        StringAssert.Contains(ex.Message, "outside the vault root");
    }

    [TestMethod]
    public void DotDotTraversalDeep_IsRejected()
    {
        var vault = NewVaultDir();
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => VaultPathGuard.EnsurePathInVault(vault, "tasks/../../escape.md"));
        StringAssert.Contains(ex.Message, "outside the vault root");
    }

    [TestMethod]
    public void AbsolutePathOutsideVault_IsRejected()
    {
        var vault = NewVaultDir();
        var outside = Path.GetTempPath(); // guaranteed outside vault subdir
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => VaultPathGuard.EnsurePathInVault(vault, outside));
        StringAssert.Contains(ex.Message, "outside the vault root");
    }

    [TestMethod]
    public void AbsolutePathWithVaultRootAsPrefix_ButOutside_IsRejected()
    {
        // vault = /tmp/.../vault  →  sibling /tmp/.../vault-sibling must be rejected
        var vaultParent = Path.Combine(Path.GetTempPath(), "glasswork-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultParent);
        var vault = Path.Combine(vaultParent, "vault");
        Directory.CreateDirectory(vault);
        var sibling = Path.Combine(vaultParent, "vault-sibling");
        Directory.CreateDirectory(sibling);

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => VaultPathGuard.EnsurePathInVault(vault, sibling));
        StringAssert.Contains(ex.Message, "outside the vault root");
    }

    [TestMethod]
    public void EmptyPath_Throws()
    {
        var vault = NewVaultDir();
        Assert.ThrowsExactly<ArgumentException>(
            () => VaultPathGuard.EnsurePathInVault(vault, ""));
    }

    [TestMethod]
    public void EmptyVault_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => VaultPathGuard.EnsurePathInVault("", "tasks/file.md"));
    }
}
