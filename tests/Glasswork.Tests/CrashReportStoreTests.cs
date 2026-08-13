using Glasswork.Core.Diagnostics;

namespace Glasswork.Tests;

[TestClass]
public class CrashReportStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-crash-reports-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Record_PersistsDiagnosticContextAndException()
    {
        var store = new CrashReportStore(_tempDir);

        var path = store.Record(
            "UI thread",
            new InvalidOperationException("Task changed before commit."),
            new CrashReportContext("1.4.3", "Windows 11", ".NET 10"));

        Assert.IsTrue(File.Exists(path));
        var report = File.ReadAllText(path);
        StringAssert.Contains(report, "Source: UI thread");
        StringAssert.Contains(report, "App version: 1.4.3");
        StringAssert.Contains(report, "OS: Windows 11");
        StringAssert.Contains(report, "Runtime: .NET 10");
        StringAssert.Contains(report, "System.InvalidOperationException: Task changed before commit.");
    }

    [TestMethod]
    public void Record_PrunesOldReportsToConfiguredLimit()
    {
        var store = new CrashReportStore(_tempDir, maxReports: 3);
        var context = new CrashReportContext("1.4.3", "Windows 11", ".NET 10");

        for (var i = 0; i < 5; i++)
            store.Record("UI thread", new InvalidOperationException($"Failure {i}"), context);

        Assert.HasCount(3, Directory.GetFiles(_tempDir, "crash-*.log"));
    }
}
