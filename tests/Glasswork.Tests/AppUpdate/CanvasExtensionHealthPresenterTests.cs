using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests.AppUpdate;

[TestClass]
public sealed class CanvasExtensionHealthPresenterTests
{
    [TestMethod]
    public void Describe_NullStatus_ReturnsNotInstalledMessage()
    {
        Assert.AreEqual(CanvasExtensionHealthPresenter.NotInstalledMessage, CanvasExtensionHealthPresenter.Describe(null));
        Assert.IsFalse(CanvasExtensionHealthPresenter.IsError(null));
    }

    [TestMethod]
    public void Describe_HealthyActivation_ReportsActiveVersion()
    {
        var status = new CanvasExtensionHealthStatus(
            "1.4.11", "1.4.11+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "bbbb", "sha", "path",
            DateTimeOffset.UtcNow, "1.4.11", "ok", null);

        Assert.AreEqual("Canvas extension 1.4.11 is active.", CanvasExtensionHealthPresenter.Describe(status));
        Assert.IsFalse(CanvasExtensionHealthPresenter.IsError(status));
    }

    [TestMethod]
    public void Describe_FailedRetryAfterPriorSuccess_MentionsBothVersionsAndIsError()
    {
        var status = new CanvasExtensionHealthStatus(
            "1.4.11", "1.4.11+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaa", "sha", "path",
            DateTimeOffset.UtcNow, "1.5.0", "failed", "identity mismatch");

        var message = CanvasExtensionHealthPresenter.Describe(status);

        StringAssert.Contains(message, "1.5.0");
        StringAssert.Contains(message, "1.4.11");
        StringAssert.Contains(message, "identity mismatch");
        Assert.IsTrue(CanvasExtensionHealthPresenter.IsError(status));
    }

    [TestMethod]
    public void Describe_NeverInstalledAndFailed_ReportsInstallationFailure()
    {
        var status = new CanvasExtensionHealthStatus(
            null, null, null, null, null,
            DateTimeOffset.UtcNow, "1.5.0", "failed", "manifest.json missing");

        var message = CanvasExtensionHealthPresenter.Describe(status);

        StringAssert.Contains(message, "Installation failed");
        StringAssert.Contains(message, "manifest.json missing");
        Assert.IsTrue(CanvasExtensionHealthPresenter.IsError(status));
    }
}
