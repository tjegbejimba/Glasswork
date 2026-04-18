using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class ActiveTaskTrackerTests
{
    [TestMethod]
    public void IsActive_ReturnsFalse_WhenNoActiveTask()
    {
        var tracker = new ActiveTaskTracker();
        Assert.IsFalse(tracker.IsActive("any-file.md"));
    }

    [TestMethod]
    public void IsActive_ReturnsTrue_WhenFileMatchesActiveTaskId()
    {
        var tracker = new ActiveTaskTracker { ActiveTaskId = "fix-login-bug" };
        Assert.IsTrue(tracker.IsActive("fix-login-bug.md"));
    }

    [TestMethod]
    public void IsActive_ReturnsTrue_WhenFileNameWithoutExtensionMatches()
    {
        var tracker = new ActiveTaskTracker { ActiveTaskId = "task-123" };
        Assert.IsTrue(tracker.IsActive("task-123"));
    }

    [TestMethod]
    public void IsActive_ReturnsFalse_WhenDifferentTask()
    {
        var tracker = new ActiveTaskTracker { ActiveTaskId = "task-a" };
        Assert.IsFalse(tracker.IsActive("task-b.md"));
    }

    [TestMethod]
    public void IsActive_IsCaseInsensitive()
    {
        var tracker = new ActiveTaskTracker { ActiveTaskId = "MyTask" };
        Assert.IsTrue(tracker.IsActive("mytask.md"));
    }

    [TestMethod]
    public void Clear_RemovesActiveTask()
    {
        var tracker = new ActiveTaskTracker { ActiveTaskId = "x" };
        tracker.Clear();
        Assert.IsNull(tracker.ActiveTaskId);
        Assert.IsFalse(tracker.IsActive("x.md"));
    }
}
