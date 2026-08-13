using System.Diagnostics;
using System.Text.Json;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class PerformanceTracerTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "glasswork-performance-tracer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void EnabledTrace_PersistsCompletedSpanAsJsonLine()
    {
        var path = Path.Combine(_tempDir, "performance.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "1",
            [PerformanceTracer.PathVariable] = path,
        };

        using var tracer = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        using (var scope = tracer.BeginSpan("test.operation"))
        {
            scope.SetCount("task_count", 3);
            scope.SetTag("view_mode", "list");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(1, lines);

        using var json = JsonDocument.Parse(lines[0]);
        var root = json.RootElement;
        Assert.AreEqual("test.operation", root.GetProperty("event").GetString());
        Assert.AreEqual("span", root.GetProperty("kind").GetString());
        Assert.AreEqual("ok", root.GetProperty("outcome").GetString());
        Assert.IsGreaterThanOrEqualTo(0, root.GetProperty("duration_ms").GetInt64());
        Assert.IsGreaterThanOrEqualTo(0, root.GetProperty("elapsed_ms").GetInt64());
        Assert.AreEqual(3, root.GetProperty("task_count").GetInt32());
        Assert.AreEqual("list", root.GetProperty("view_mode").GetString());
        Assert.IsTrue(root.TryGetProperty("thread_id", out _));
        Assert.IsTrue(root.TryGetProperty("session_id", out _));
        Assert.IsTrue(root.TryGetProperty("ts", out _));
    }

    [TestMethod]
    public void DisabledTrace_DoesNotCreateOutput()
    {
        var path = Path.Combine(_tempDir, "disabled.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "0",
            [PerformanceTracer.PathVariable] = path,
        };

        using var tracer = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        using (tracer.BeginSpan("test.disabled")) { }
        tracer.EmitMilestone("test.disabled_milestone");

        Assert.IsFalse(tracer.IsEnabled);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void FailedSpan_RecordsErrorOutcome()
    {
        var path = Path.Combine(_tempDir, "error.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "1",
            [PerformanceTracer.PathVariable] = path,
        };

        using var tracer = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        using (var scope = tracer.BeginSpan("test.failed"))
        {
            scope.SetOutcome("error");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var json = JsonDocument.Parse(stream);
        Assert.AreEqual("error", json.RootElement.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void ConcurrentSpans_ProduceCompleteJsonLines()
    {
        var path = Path.Combine(_tempDir, "concurrent.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "1",
            [PerformanceTracer.PathVariable] = path,
        };

        var tracer = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        Parallel.For(0, 20, index =>
        {
            using var scope = tracer.BeginSpan("test.concurrent");
            scope.SetCount("item", index);
        });
        tracer.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.HasCount(20, lines);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            Assert.AreEqual("test.concurrent", json.RootElement.GetProperty("event").GetString());
        }
    }

    [TestMethod]
    public void CancelledSpan_DoesNotEmitARecord()
    {
        var path = Path.Combine(_tempDir, "cancelled.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "1",
            [PerformanceTracer.PathVariable] = path,
        };

        using var tracer = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        using (var scope = tracer.BeginSpan("test.cancelled"))
        {
            scope.Cancel();
        }

        Assert.AreEqual(0, new FileInfo(path).Length);
    }

    [TestMethod]
    public void Metadata_CannotOverwriteTraceEnvelope()
    {
        var path = Path.Combine(_tempDir, "reserved-metadata.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "1",
            [PerformanceTracer.PathVariable] = path,
        };

        using var tracer = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        using var scope = tracer.BeginSpan("test.reserved_metadata");

        Assert.ThrowsExactly<ArgumentException>(() => scope.SetTag("event", "replacement"));
    }

    [TestMethod]
    public void DisposedTracer_IgnoresLateMeasurements()
    {
        var path = Path.Combine(_tempDir, "disposed.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "1",
            [PerformanceTracer.PathVariable] = path,
        };

        var tracer = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        tracer.Dispose();

        using (tracer.BeginSpan("test.late")) { }
        tracer.EmitMilestone("test.late_milestone");

        Assert.AreEqual(0, new FileInfo(path).Length);
    }

    [TestMethod]
    public void ExplicitPath_AllowsOnlyOneWriter()
    {
        var path = Path.Combine(_tempDir, "exclusive.jsonl");
        var environment = new Dictionary<string, string?>
        {
            [PerformanceTracer.EnabledVariable] = "1",
            [PerformanceTracer.PathVariable] = path,
        };

        using var first = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());
        using var second = PerformanceTracer.CreateFromEnvironment(
            environment,
            Stopwatch.GetTimestamp());

        Assert.IsTrue(first.IsEnabled);
        Assert.IsFalse(second.IsEnabled);
    }
}
