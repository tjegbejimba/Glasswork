using Microsoft.VisualStudio.TestTools.UnitTesting;
using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests;

[TestClass]
public class UpdateStatusPresenterTests
{
    [TestMethod]
    public void Describe_UpToDate_ReturnsLatestVersionMessage()
    {
        AppVersion.TryParse("1.3.0", out var installed);
        AppVersion.TryParse("1.3.0", out var available);
        var result = UpdateCheckResult.Compare(installed!, available!);

        Assert.AreEqual("You're on the latest version.", UpdateStatusPresenter.Describe(result));
    }

    [TestMethod]
    public void Describe_UpdateAvailable_ReturnsAvailableVersionMessage()
    {
        AppVersion.TryParse("1.3.0", out var installed);
        AppVersion.TryParse("1.4.0", out var available);
        var result = UpdateCheckResult.Compare(installed!, available!);

        Assert.AreEqual("Glasswork 1.4.0 is available.", UpdateStatusPresenter.Describe(result));
    }

    [TestMethod]
    public void Describe_CheckFailed_ReturnsFailureMessage()
    {
        var result = UpdateCheckResult.Failed("Network error");

        Assert.AreEqual("Couldn't check for updates.", UpdateStatusPresenter.Describe(result));
    }

    [TestMethod]
    public void Describe_NullResult_ReturnsNotCheckedMessage()
    {
        Assert.AreEqual("Not checked yet.", UpdateStatusPresenter.Describe(null));
    }
}
