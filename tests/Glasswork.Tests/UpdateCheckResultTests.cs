using Microsoft.VisualStudio.TestTools.UnitTesting;
using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests;

[TestClass]
public class UpdateCheckResultTests
{
    [TestMethod]
    public void CompareVersions_AvailableGreater_ReturnsUpdateAvailable()
    {
        AppVersion.TryParse("1.3.0", out var installed);
        AppVersion.TryParse("1.4.0", out var available);

        var result = UpdateCheckResult.Compare(installed!, available!);

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.IsFalse(result.IsUpToDate);
        Assert.IsFalse(result.IsCheckFailed);
        Assert.AreEqual(available, result.AvailableVersion);
    }

    [TestMethod]
    public void CompareVersions_AvailableEqual_ReturnsUpToDate()
    {
        AppVersion.TryParse("1.3.0", out var installed);
        AppVersion.TryParse("1.3.0", out var available);

        var result = UpdateCheckResult.Compare(installed!, available!);

        Assert.IsTrue(result.IsUpToDate);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.IsFalse(result.IsCheckFailed);
    }

    [TestMethod]
    public void CompareVersions_AvailableLess_ReturnsUpToDate()
    {
        AppVersion.TryParse("1.4.0", out var installed);
        AppVersion.TryParse("1.3.0", out var available);

        var result = UpdateCheckResult.Compare(installed!, available!);

        Assert.IsTrue(result.IsUpToDate);
        Assert.IsFalse(result.IsUpdateAvailable);
    }

    [TestMethod]
    public void CheckFailed_ReturnsFailedResult()
    {
        var result = UpdateCheckResult.Failed("Network error");

        Assert.IsTrue(result.IsCheckFailed);
        Assert.IsFalse(result.IsUpToDate);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.AreEqual("Network error", result.FailureReason);
    }
}
