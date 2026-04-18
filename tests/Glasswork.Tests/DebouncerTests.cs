using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class DebouncerTests
{
    [TestMethod]
    public void Trigger_FiresActionAfterQuietPeriod()
    {
        int count = 0;
        var signal = new ManualResetEventSlim(false);
        using var debouncer = new Debouncer(TimeSpan.FromMilliseconds(100), () =>
        {
            Interlocked.Increment(ref count);
            signal.Set();
        });

        debouncer.Trigger();

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(2)), "Action should fire");
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void Trigger_CoalescesRapidCallsIntoSingleAction()
    {
        int count = 0;
        var signal = new ManualResetEventSlim(false);
        using var debouncer = new Debouncer(TimeSpan.FromMilliseconds(200), () =>
        {
            Interlocked.Increment(ref count);
            signal.Set();
        });

        // 5 rapid triggers within 100ms (well under the 200ms quiet window)
        for (int i = 0; i < 5; i++)
        {
            debouncer.Trigger();
            Thread.Sleep(20);
        }

        Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(2)), "Action should eventually fire");
        // Give a little slack to be sure no second fire is in flight
        Thread.Sleep(150);
        Assert.AreEqual(1, count, "Rapid triggers should coalesce into exactly one action");
    }

    [TestMethod]
    public void Trigger_FiresAgainAfterQuietPeriodElapses()
    {
        int count = 0;
        using var debouncer = new Debouncer(TimeSpan.FromMilliseconds(80), () =>
        {
            Interlocked.Increment(ref count);
        });

        debouncer.Trigger();
        Thread.Sleep(250); // wait for first fire
        debouncer.Trigger();
        Thread.Sleep(250); // wait for second fire

        Assert.AreEqual(2, count, "Two distinct trigger bursts should each fire once");
    }

    [TestMethod]
    public void Dispose_CancelsPendingAction()
    {
        int count = 0;
        var debouncer = new Debouncer(TimeSpan.FromMilliseconds(200), () =>
        {
            Interlocked.Increment(ref count);
        });

        debouncer.Trigger();
        debouncer.Dispose();
        Thread.Sleep(400);

        Assert.AreEqual(0, count, "Disposed debouncer should not fire pending action");
    }
}
