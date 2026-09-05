namespace Glasswork.CanvasHost.Tests;

[TestClass]
public sealed class CanvasHostTestAssembly
{
    [AssemblyInitialize]
    public static void Initialize(TestContext testContext)
    {
        var diagnosticsRoot = CanvasHostTestSupport.ResetDiagnosticsDirectory();
        testContext.WriteLine($"CanvasHost diagnostics output: {diagnosticsRoot}");
    }
}
