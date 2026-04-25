using System.IO;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultValidatorTests
{
    private static string NewTempDir(string suffix = "")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"glasswork-validator{suffix}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void Validate_ReturnsNotFound_WhenPathDoesNotExist()
    {
        var path = Path.Combine(Path.GetTempPath(), "glasswork-nonexistent-" + Guid.NewGuid());
        var result = VaultValidator.Validate(path);
        Assert.AreEqual(VaultValidationResult.NotFound, result);
    }

    [TestMethod]
    public void Validate_ReturnsNotFound_WhenPathIsEmpty()
    {
        Assert.AreEqual(VaultValidationResult.NotFound, VaultValidator.Validate(""));
        Assert.AreEqual(VaultValidationResult.NotFound, VaultValidator.Validate("   "));
    }

    [TestMethod]
    public void Validate_ReturnsValid_WhenObsidianFolderPresent()
    {
        var dir = NewTempDir("-obsidian");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".obsidian"));
            Assert.AreEqual(VaultValidationResult.Valid, VaultValidator.Validate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_ReturnsValid_WhenObsidianFolderAndMarkdownFilesPresent()
    {
        var dir = NewTempDir("-obsidian-md");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".obsidian"));
            File.WriteAllText(Path.Combine(dir, "note.md"), "# Note");
            Assert.AreEqual(VaultValidationResult.Valid, VaultValidator.Validate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_ReturnsHasMarkdownFiles_WhenOnlyMarkdownFilesPresent()
    {
        var dir = NewTempDir("-mdonly");
        try
        {
            File.WriteAllText(Path.Combine(dir, "note.md"), "# Note");
            Assert.AreEqual(VaultValidationResult.HasMarkdownFiles, VaultValidator.Validate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_ReturnsEmpty_WhenDirectoryExistsButIsEmpty()
    {
        var dir = NewTempDir("-empty");
        try
        {
            Assert.AreEqual(VaultValidationResult.Empty, VaultValidator.Validate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_ReturnsEmpty_WhenDirectoryHasNonMarkdownFilesOnly()
    {
        var dir = NewTempDir("-nonmd");
        try
        {
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a vault");
            Assert.AreEqual(VaultValidationResult.Empty, VaultValidator.Validate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_DoesNotSearchSubdirectories_ForMarkdownFiles()
    {
        var dir = NewTempDir("-subdir");
        try
        {
            // .md file in a subdirectory should NOT influence the result (no .obsidian)
            var sub = Directory.CreateDirectory(Path.Combine(dir, "notes"));
            File.WriteAllText(Path.Combine(sub.FullName, "note.md"), "# Note");
            Assert.AreEqual(VaultValidationResult.Empty, VaultValidator.Validate(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
