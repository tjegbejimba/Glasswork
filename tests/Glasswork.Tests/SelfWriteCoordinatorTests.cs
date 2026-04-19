using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class SelfWriteCoordinatorTests
{
    [TestMethod]
    public void RegisterWrite_MakesPathSuppressed()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        coord.RegisterWrite(@"C:\vault\task.md");

        Assert.IsTrue(coord.IsSuppressed(@"C:\vault\task.md"));
    }

    [TestMethod]
    public void IsSuppressed_FalseForUnregisteredPath()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        Assert.IsFalse(coord.IsSuppressed(@"C:\vault\other.md"));
    }

    [TestMethod]
    public void IsSuppressed_FalseAfterTtlExpires()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(50));
        coord.RegisterWrite(@"C:\vault\task.md");

        Thread.Sleep(150);

        Assert.IsFalse(coord.IsSuppressed(@"C:\vault\task.md"));
    }

    [TestMethod]
    public void IsSuppressed_IsCaseInsensitive()
    {
        var coord = new SelfWriteCoordinator(TimeSpan.FromMilliseconds(500));
        coord.RegisterWrite(@"C:\Vault\Task.md");

        Assert.IsTrue(coord.IsSuppressed(@"c:\vault\task.md"));
    }
}
