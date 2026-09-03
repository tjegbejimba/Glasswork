using Glasswork.Core.AppUpdate;

namespace Glasswork.Tests.AppUpdate;

[TestClass]
public sealed class CanvasExtensionHealthStatusTests
{
    [TestMethod]
    public void Parse_HealthyActivation_ReadsVersionIdentityAndOkAttempt()
    {
        var json = """
        {
          "version": "1.4.11",
          "identity": "1.4.11+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "sourceRevision": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "sha256": "aaaa",
          "hostExecutablePath": "C:\\host\\Glasswork.CanvasHost.exe",
          "lastAttempt": {
            "utc": "2026-09-03T12:00:00Z",
            "version": "1.4.11",
            "status": "ok",
            "message": null
          }
        }
        """;

        var status = CanvasExtensionHealthStatus.Parse(json);

        Assert.IsNotNull(status);
        Assert.AreEqual("1.4.11", status!.Version);
        Assert.AreEqual("1.4.11+bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", status.Identity);
        Assert.AreEqual("ok", status.LastAttemptStatus);
        Assert.IsFalse(status.LastAttemptFailed);
        Assert.IsTrue(status.HasActivatedVersion);
    }

    [TestMethod]
    public void Parse_FailedAttemptAfterPriorSuccess_PreservesPreviousVersionAndReportsFailure()
    {
        var json = """
        {
          "version": "1.4.11",
          "identity": "1.4.11+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "sourceRevision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "sha256": "aaaa",
          "hostExecutablePath": "C:\\host\\Glasswork.CanvasHost.exe",
          "lastAttempt": {
            "utc": "2026-09-03T12:05:00Z",
            "version": "1.5.0",
            "status": "failed",
            "message": "Staged canvas host identity did not match."
          }
        }
        """;

        var status = CanvasExtensionHealthStatus.Parse(json);

        Assert.IsNotNull(status);
        Assert.AreEqual("1.4.11", status!.Version, "a failed retry must not erase the last known-good version");
        Assert.IsTrue(status.LastAttemptFailed);
        Assert.AreEqual("1.5.0", status.LastAttemptVersion);
        Assert.AreEqual("Staged canvas host identity did not match.", status.LastAttemptMessage);
    }

    [TestMethod]
    public void Parse_NeverInstalled_HasNoActivatedVersionButRecordsFailure()
    {
        var json = """
        {
          "version": null,
          "identity": null,
          "sourceRevision": null,
          "sha256": null,
          "hostExecutablePath": null,
          "lastAttempt": {
            "utc": "2026-09-03T12:05:00Z",
            "version": "1.5.0",
            "status": "failed",
            "message": "Canvas extension bundle is missing manifest.json."
          }
        }
        """;

        var status = CanvasExtensionHealthStatus.Parse(json);

        Assert.IsNotNull(status);
        Assert.IsFalse(status!.HasActivatedVersion);
        Assert.IsTrue(status.LastAttemptFailed);
    }

    [TestMethod]
    public void Parse_EmptyString_ReturnsNull()
    {
        Assert.IsNull(CanvasExtensionHealthStatus.Parse(string.Empty));
    }
}
