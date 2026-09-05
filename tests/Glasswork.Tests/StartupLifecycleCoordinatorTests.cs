using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class StartupLifecycleCoordinatorTests
{
    [TestMethod]
    public void NewAttempt_RejectsAndDisposesStaleCompletion()
    {
        using var coordinator =
            new StartupLifecycleCoordinator<TestServices, string>();
        var staleAttempt = coordinator.BeginAttempt(resetAttempt: true);
        var currentAttempt = coordinator.BeginAttempt(resetAttempt: false);
        var staleServices = new TestServices();
        var currentServices = new TestServices();

        Assert.IsFalse(coordinator.TryPublish(staleAttempt, staleServices));
        Assert.IsTrue(staleServices.IsDisposed);
        Assert.IsTrue(coordinator.TryPublish(currentAttempt, currentServices));
        Assert.IsFalse(currentServices.IsDisposed);
    }

    [TestMethod]
    public void Dispose_CancelsLoadingAndRejectsLateCompletion()
    {
        var coordinator =
            new StartupLifecycleCoordinator<TestServices, string>();
        var attempt = coordinator.BeginAttempt(resetAttempt: true);
        var services = new TestServices();

        coordinator.Dispose();

        Assert.IsTrue(attempt.CancellationToken.IsCancellationRequested);
        Assert.IsFalse(coordinator.TryPublish(attempt, services));
        Assert.IsTrue(services.IsDisposed);
    }

    [TestMethod]
    public void Retry_PreservesPendingNavigationAndDrainsItOnce()
    {
        using var coordinator =
            new StartupLifecycleCoordinator<TestServices, string>();
        coordinator.EnqueueNavigation("glasswork://task/pending");
        _ = coordinator.BeginAttempt(resetAttempt: true);
        var retry = coordinator.BeginAttempt(resetAttempt: false);
        Assert.IsTrue(coordinator.TryPublish(retry, new TestServices()));
        var delivered = new List<string>();

        var firstCount = coordinator.DrainNavigation(
            retry,
            uri =>
            {
                delivered.Add(uri);
                return true;
            });
        var secondCount = coordinator.DrainNavigation(
            retry,
            uri =>
            {
                delivered.Add(uri);
                return true;
            });

        Assert.AreEqual(1, firstCount);
        Assert.AreEqual(0, secondCount);
        CollectionAssert.AreEqual(
            new[] { "glasswork://task/pending" },
            delivered);
    }

    [TestMethod]
    public void SameAttempt_CannotPublishTwice()
    {
        using var coordinator =
            new StartupLifecycleCoordinator<TestServices, string>();
        var attempt = coordinator.BeginAttempt(resetAttempt: true);
        var first = new TestServices();
        var duplicate = new TestServices();

        Assert.IsTrue(coordinator.TryPublish(attempt, first));
        Assert.IsFalse(coordinator.TryPublish(attempt, duplicate));
        Assert.IsFalse(first.IsDisposed);
        Assert.IsTrue(duplicate.IsDisposed);
    }

    private sealed class TestServices : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
