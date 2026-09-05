using Glasswork.Core.VisualVerification;

namespace Glasswork.Tests;

[TestClass]
public sealed class VisualVerificationMergeEvidenceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "glasswork-evidence-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void CaptureLaunchBundle_UsesStableRelativePathsAndHashesEveryFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "runtimes", "win-x64", "native"));
        File.WriteAllText(Path.Combine(_root, "Glasswork.dll"), "managed");
        File.WriteAllText(
            Path.Combine(_root, "runtimes", "win-x64", "native", "Microsoft.ui.xaml.dll"),
            "native");

        var manifest = VisualVerificationMergeEvidence.CaptureLaunchBundle(_root);

        CollectionAssert.AreEqual(
            new[]
            {
                "Glasswork.dll",
                "runtimes/win-x64/native/Microsoft.ui.xaml.dll",
            },
            manifest.Files.Select(file => file.Path).ToArray());
        Assert.IsTrue(manifest.Files.All(file => file.Sha256.Length == 64));
        Assert.AreEqual(64, manifest.Sha256.Length);
    }

    [TestMethod]
    public void EnsureLaunchBundleUnchanged_WhenAFileChanges_Throws()
    {
        var assembly = Path.Combine(_root, "Glasswork.dll");
        File.WriteAllText(assembly, "before");
        var before = VisualVerificationMergeEvidence.CaptureLaunchBundle(_root);
        File.WriteAllText(assembly, "after");
        var after = VisualVerificationMergeEvidence.CaptureLaunchBundle(_root);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => VisualVerificationMergeEvidence.EnsureLaunchBundleUnchanged(before, after));

        StringAssert.Contains(exception.Message, "changed during visual verification");
    }

    [TestMethod]
    public void CaptureLaunchBundle_IncludesCanvasRetryExecutableStagedUnderLaunchRoot()
    {
        var retryHost = Path.Combine(
            _root,
            "VisualVerification",
            "canvas-retry-bundle",
            "host",
            "9.9.9",
            "Glasswork.CanvasHost.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(retryHost)!);
        File.WriteAllText(retryHost, "canvas host");

        var manifest = VisualVerificationMergeEvidence.CaptureLaunchBundle(_root);

        CollectionAssert.Contains(
            manifest.Files.Select(file => file.Path).ToArray(),
            "VisualVerification/canvas-retry-bundle/host/9.9.9/Glasswork.CanvasHost.exe");
    }

    [TestMethod]
    public void CaptureVerifiedAuxiliaryBundle_BindsInstalledCopy()
    {
        var source = Path.Combine(_root, "source");
        var installed = Path.Combine(_root, "installed");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(source, "Glasswork.CanvasHost.exe"), "canvas host");
        File.WriteAllText(Path.Combine(source, "Glasswork.CanvasHost.dll"), "managed host");
        File.Copy(
            Path.Combine(source, "Glasswork.CanvasHost.exe"),
            Path.Combine(installed, "Glasswork.CanvasHost.exe"));
        File.Copy(
            Path.Combine(source, "Glasswork.CanvasHost.dll"),
            Path.Combine(installed, "Glasswork.CanvasHost.dll"));
        var expected = VisualVerificationMergeEvidence.CaptureLaunchBundle(source);

        var installedFiles = VisualVerificationMergeEvidence.CaptureVerifiedAuxiliaryBundle(
            expected,
            installed,
            "canvas-extension-retry-installed/host/9.9.9");

        CollectionAssert.AreEqual(
            new[]
            {
                "canvas-extension-retry-installed/host/9.9.9/Glasswork.CanvasHost.dll",
                "canvas-extension-retry-installed/host/9.9.9/Glasswork.CanvasHost.exe",
            },
            installedFiles.Select(file => file.Path).ToArray());
    }

    [TestMethod]
    public void CaptureVerifiedAuxiliaryBundle_WhenInstalledCopyChanges_Throws()
    {
        var source = Path.Combine(_root, "source");
        var installed = Path.Combine(_root, "installed");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(source, "Glasswork.CanvasHost.exe"), "expected");
        File.WriteAllText(Path.Combine(installed, "Glasswork.CanvasHost.exe"), "modified");
        var expected = VisualVerificationMergeEvidence.CaptureLaunchBundle(source);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => VisualVerificationMergeEvidence.CaptureVerifiedAuxiliaryBundle(
                expected,
                installed,
                "canvas-extension-retry-installed/host/9.9.9"));

        StringAssert.Contains(exception.Message, "installed auxiliary bundle");
    }

    [TestMethod]
    public void EnsureSourceUnchanged_WhenScenarioChanges_Throws()
    {
        var before = new VisualVerificationSourceSnapshot(
            "0123456789012345678901234567890123456789",
            "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
            "",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var after = before with
        {
            ScenarioSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        };

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => VisualVerificationMergeEvidence.EnsureSourceUnchanged(before, after));

        StringAssert.Contains(exception.Message, "source or scenario changed");
    }

    [TestMethod]
    public void CaptureSourceSnapshot_WhenScenarioIsOutsideRepository_Throws()
    {
        var scenario = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(scenario, "{}");
        try
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => VisualVerificationMergeEvidence.CaptureSourceSnapshot(_root, scenario));

            StringAssert.Contains(exception.Message, "under the repository root");
        }
        finally
        {
            File.Delete(scenario);
        }
    }
}
