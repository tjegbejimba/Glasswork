using Glasswork.Core.Models;

namespace Glasswork.Tests;

[TestClass]
public class ObsidianUriBuilderTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"C:\Users\me\Wiki\wiki" : "/home/me/Wiki/wiki";
    private static string VaultRootWithSpace => OperatingSystem.IsWindows() ? @"C:\Users\me\My Wiki" : "/home/me/My Wiki";
    private static string Sep => Path.DirectorySeparatorChar.ToString();

    [TestMethod]
    public void BuildsForBasicArtifactPath()
    {
        var path = Path.Combine(Root, "todo", "TASK-1.artifacts", "plan.md");
        var uri = ObsidianUriBuilder.ForArtifact(Root, "Wiki", path);
        Assert.AreEqual("obsidian://open?vault=Wiki&file=todo/TASK-1.artifacts/plan", uri);
    }

    [TestMethod]
    public void EncodesSpacesAndSpecialChars()
    {
        var path = Path.Combine(Root, "todo", "TASK 1.artifacts", "my plan & notes.md");
        var uri = ObsidianUriBuilder.ForArtifact(Root, "Wiki", path);
        Assert.AreEqual("obsidian://open?vault=Wiki&file=todo/TASK%201.artifacts/my%20plan%20%26%20notes", uri);
    }

    [TestMethod]
    public void StripsMdExtensionCaseInsensitive()
    {
        var path = Path.Combine(Root, "todo", "T.artifacts", "Plan.MD");
        var uri = ObsidianUriBuilder.ForArtifact(Root, "Wiki", path);
        StringAssert.EndsWith(uri ?? "", "/Plan");
    }

    [TestMethod]
    public void ReturnsNull_WhenArtifactOutsideVault()
    {
        var outside = OperatingSystem.IsWindows() ? @"C:\Other\plan.md" : "/other/plan.md";
        Assert.IsNull(ObsidianUriBuilder.ForArtifact(Root, "Wiki", outside));
    }

    [TestMethod]
    public void ReturnsNull_OnEmptyInputs()
    {
        var path = Path.Combine(Root, "todo", "T.artifacts", "p.md");
        Assert.IsNull(ObsidianUriBuilder.ForArtifact("", "Wiki", path));
        Assert.IsNull(ObsidianUriBuilder.ForArtifact(Root, "", path));
        Assert.IsNull(ObsidianUriBuilder.ForArtifact(Root, "Wiki", ""));
        Assert.IsNull(ObsidianUriBuilder.ForArtifact(null!, "Wiki", path));
    }

    [TestMethod]
    public void ToleratesTrailingSeparatorOnRoot()
    {
        var rootWithSep = Root + Sep;
        var path = Path.Combine(Root, "todo", "T.artifacts", "p.md");
        var uri = ObsidianUriBuilder.ForArtifact(rootWithSep, "Wiki", path);
        Assert.AreEqual("obsidian://open?vault=Wiki&file=todo/T.artifacts/p", uri);
    }

    [TestMethod]
    public void EncodesVaultName()
    {
        var path = Path.Combine(Root, "p.md");
        var uri = ObsidianUriBuilder.ForArtifact(Root, "My Vault", path);
        StringAssert.Contains(uri ?? "", "vault=My%20Vault");
    }

    [TestMethod]
    public void ForVaultRelativePath_DerivesVaultName_AndEncodesCommonInputs()
    {
        var relative = $".{Sep}wiki{Sep}todo{Sep}Nested Folder{Sep}設計.md";
        var uri = ObsidianUriBuilder.ForVaultRelativePath(VaultRootWithSpace, relative);
        Assert.AreEqual(
            "obsidian://open?vault=My%20Wiki&file=wiki/todo/Nested%20Folder/%E8%A8%AD%E8%A8%88",
            uri);
    }

    [TestMethod]
    public void ForVaultRelativePath_ReturnsNull_WhenPathEscapesVault()
    {
        Assert.IsNull(ObsidianUriBuilder.ForVaultRelativePath(VaultRootWithSpace, $"..{Sep}outside.md"));
    }

    [TestMethod]
    public void ForVaultRelativePath_TaskFileUnderWikiTodo_StaysTodoRelative()
    {
        var absoluteTaskPath = Path.Combine(Root, "todo", "TASK-1.md");
        var uri = ObsidianUriBuilder.ForVaultRelativePath(Root, absoluteTaskPath);
        Assert.AreEqual("obsidian://open?vault=wiki&file=todo/TASK-1", uri);
    }
}
