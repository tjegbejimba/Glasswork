namespace Glasswork.CanvasHost.Tests;

public abstract class CanvasHostTestBase
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void InitializeCanvasHostTest() =>
        CanvasHostTestSupport.BeginTest(TestContext);

    [TestCleanup]
    public Task CleanupCanvasHostTest() =>
        CanvasHostTestSupport.EndTestAsync(TestContext);
}
