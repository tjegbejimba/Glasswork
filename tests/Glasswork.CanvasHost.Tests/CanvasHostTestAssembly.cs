namespace Glasswork.CanvasHost.Tests;

[TestClass]
public sealed class CanvasHostTestAssembly
{
    [AssemblyInitialize]
    public static void Initialize(TestContext _) =>
        CanvasHostTestSupport.ResetDiagnosticsDirectory();
}
