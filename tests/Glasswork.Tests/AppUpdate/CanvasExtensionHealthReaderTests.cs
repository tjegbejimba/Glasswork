using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests.AppUpdate;

[TestClass]
public sealed class CanvasExtensionHealthReaderTests
{
    [TestMethod]
    public void ResolveExtensionsRoot_HonorsOverrideVariable()
    {
        var resolved = CanvasExtensionHealthReader.ResolveExtensionsRoot(
            name => name == CanvasExtensionHealthReader.ExtensionsRootOverrideVariable ? @"C:\fixture\extensions" : null);

        Assert.AreEqual(@"C:\fixture\extensions", resolved);
    }

    [TestMethod]
    public void ResolveExtensionsRoot_FallsBackToCopilotHome_WhenOverrideAbsent()
    {
        var resolved = CanvasExtensionHealthReader.ResolveExtensionsRoot(
            name => name == "COPILOT_HOME" ? @"C:\copilot-home" : null);

        Assert.AreEqual(Path.Combine(@"C:\copilot-home", "extensions"), resolved);
    }

    [TestMethod]
    public void ResolveExtensionsRoot_FallsBackToUserProfileDotCopilot_WhenNothingConfigured()
    {
        var resolved = CanvasExtensionHealthReader.ResolveExtensionsRoot(_ => null);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "extensions");
        Assert.AreEqual(expected, resolved);
    }

    [TestMethod]
    public void Read_MissingCurrentJson_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "glasswork-canvas-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsNull(CanvasExtensionHealthReader.Read(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Read_ValidCurrentJson_ReturnsParsedStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), "glasswork-canvas-health-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(root, CanvasExtensionHealthReader.ExtensionName);
        Directory.CreateDirectory(extensionDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(extensionDirectory, "current.json"),
                """{"version":"1.4.11","identity":"1.4.11+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","lastAttempt":{"status":"ok"}}""");

            var status = CanvasExtensionHealthReader.Read(root);

            Assert.IsNotNull(status);
            Assert.AreEqual("1.4.11", status!.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Read_MalformedCurrentJson_ReturnsNullInsteadOfThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), "glasswork-canvas-health-" + Guid.NewGuid().ToString("N"));
        var extensionDirectory = Path.Combine(root, CanvasExtensionHealthReader.ExtensionName);
        Directory.CreateDirectory(extensionDirectory);
        try
        {
            File.WriteAllText(Path.Combine(extensionDirectory, "current.json"), "{not json");

            Assert.IsNull(CanvasExtensionHealthReader.Read(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
