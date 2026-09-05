using Glasswork.Core.AppUpdate;

namespace Glasswork.CanvasHost.Tests;

[TestClass]
public sealed class CanvasVersionDriftDetectorTests : CanvasHostTestBase
{
    [TestMethod]
    public void Detect_NoRecordedState_IsNotDrift()
    {
        var (detected, message) = CanvasVersionDriftDetector.Detect(currentState: null, ownIdentity: "1.4.11+aaa");

        Assert.IsFalse(detected);
        Assert.IsNull(message);
    }

    [TestMethod]
    public void Detect_MatchingIdentity_IsNotDrift()
    {
        var state = new CanvasExtensionHealthStatus("1.4.11", "1.4.11+aaa", "aaa", "sha", "path", null, null, "ok", null);

        var (detected, message) = CanvasVersionDriftDetector.Detect(state, ownIdentity: "1.4.11+aaa");

        Assert.IsFalse(detected);
        Assert.IsNull(message);
    }

    [TestMethod]
    public void Detect_NewerIdentityActivated_IsDriftWithNonBlockingMessage()
    {
        var state = new CanvasExtensionHealthStatus("1.5.0", "1.5.0+bbb", "bbb", "sha", "path", null, null, "ok", null);

        var (detected, message) = CanvasVersionDriftDetector.Detect(state, ownIdentity: "1.4.11+aaa");

        Assert.IsTrue(detected);
        StringAssert.Contains(message, "reopen", System.StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void Detect_FailedRetryStateWithNoIdentity_IsNotDrift()
    {
        // A failed attempt that never successfully activated anything (identity
        // null) must not be mistaken for drift — there is nothing newer running.
        var state = new CanvasExtensionHealthStatus(null, null, null, null, null, null, "1.5.0", "failed", "boom");

        var (detected, _) = CanvasVersionDriftDetector.Detect(state, ownIdentity: "1.4.11+aaa");

        Assert.IsFalse(detected);
    }

    [TestMethod]
    public void ResolveDefaultCurrentStatePath_UnderHostVersionDirectory_ResolvesExtensionDirectoryCurrentJson()
    {
        var baseDirectory = Path.Combine("C:", "extensions", "glasswork-task-viewer", "host", "1.4.11");

        var resolved = CanvasVersionDriftDetector.ResolveDefaultCurrentStatePath(baseDirectory);

        Assert.AreEqual(
            Path.Combine("C:", "extensions", "glasswork-task-viewer", "current.json"),
            resolved);
    }

    [TestMethod]
    public void ResolveDefaultCurrentStatePath_UnrelatedDirectoryShape_ReturnsNull()
    {
        var resolved = CanvasVersionDriftDetector.ResolveDefaultCurrentStatePath(Path.Combine("C:", "repo", "bin", "Debug", "net10.0"));

        Assert.IsNull(resolved);
    }
}
