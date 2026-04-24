using System.Text;
using System.Text.Json;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class McpLoggerTests
{
    private string _vaultDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-logger-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    private McpLogger MakeLogger(StringBuilder? stderrSink = null, bool fileEnabled = false, bool traceEnabled = false)
    {
        var writer = stderrSink is null ? TextWriter.Null : new StringWriter(stderrSink);
        return new McpLogger(_vaultDir, writer, fileEnabled, traceEnabled);
    }

    private GlassworkTools MakeTools(McpLogger logger) =>
        new GlassworkTools(new VaultContext(_vaultDir), logger);

    // ─────────────────────── Layer 1: stderr log line ────────────────────

    [TestMethod]
    public void ToolCall_AlwaysWritesOneJsonLineToStderr()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink));

        tools.ListTasks();

        var lines = sink.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length, "Exactly one log line per tool call.");
        Assert.IsTrue(IsValidJson(lines[0]), "Log line must be valid JSON.");
    }

    [TestMethod]
    public void ToolCall_LogLine_ContainsRequiredFields()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink));

        tools.ListTasks();

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        var root = doc.RootElement;

        Assert.IsTrue(root.TryGetProperty("ts", out var ts), "Must have 'ts'.");
        Assert.AreEqual(JsonValueKind.String, ts.ValueKind);

        Assert.IsTrue(root.TryGetProperty("tool", out var tool), "Must have 'tool'.");
        Assert.AreEqual("list_tasks", tool.GetString());

        Assert.IsTrue(root.TryGetProperty("duration_ms", out var dur), "Must have 'duration_ms'.");
        Assert.IsTrue(dur.GetInt64() >= 0);

        Assert.IsTrue(root.TryGetProperty("result", out var result), "Must have 'result'.");
        Assert.AreEqual("ok", result.GetString());
    }

    [TestMethod]
    public void ListTasks_LogLine_IncludesTaskCount()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink));

        tools.AddTask("A");
        sink.Clear(); // only inspect the list_tasks line

        tools.ListTasks();

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        Assert.IsTrue(doc.RootElement.TryGetProperty("task_count", out var tc), "Must have 'task_count'.");
        Assert.AreEqual(1, tc.GetInt32());
    }

    [TestMethod]
    public void AddTask_LogLine_HasToolName()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink));

        tools.AddTask("My Task");

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        Assert.AreEqual("add_task", doc.RootElement.GetProperty("tool").GetString());
    }

    [TestMethod]
    public void ToolCall_ErrorResult_WhenToolThrows()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink));

        try { tools.ListTasks(status: "bad"); } catch (ArgumentException) { }

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        Assert.AreEqual("error", doc.RootElement.GetProperty("result").GetString());
    }

    [TestMethod]
    public void ToolCall_WithoutLogger_DoesNotThrow()
    {
        var tools = new GlassworkTools(new VaultContext(_vaultDir)); // no logger
        // Should work normally without any logger attached.
        var json = tools.ListTasks();
        Assert.IsNotNull(json);
    }

    // ─────────────────────── Layer 1: file sink ──────────────────────────

    [TestMethod]
    public void FileEnabled_WritesToLogFile()
    {
        var tools = MakeTools(MakeLogger(fileEnabled: true));

        tools.ListTasks();

        var logPath = Path.Combine(_vaultDir, ".glasswork", "mcp.log");
        Assert.IsTrue(File.Exists(logPath), "mcp.log must be created when GLASSWORK_MCP_LOG=1.");

        var lines = File.ReadAllLines(logPath);
        Assert.AreEqual(1, lines.Length);
        Assert.IsTrue(IsValidJson(lines[0]));
    }

    [TestMethod]
    public void FileDisabled_DoesNotWriteLogFile()
    {
        var tools = MakeTools(MakeLogger(fileEnabled: false));

        tools.ListTasks();

        var logPath = Path.Combine(_vaultDir, ".glasswork", "mcp.log");
        Assert.IsFalse(File.Exists(logPath), "mcp.log must not be created when GLASSWORK_MCP_LOG is not set.");
    }

    [TestMethod]
    public void FileEnabled_MultipleCallsAppendLines()
    {
        var tools = MakeTools(MakeLogger(fileEnabled: true));

        tools.AddTask("T1");
        tools.AddTask("T2");
        tools.ListTasks();

        var logPath = Path.Combine(_vaultDir, ".glasswork", "mcp.log");
        var lines = File.ReadAllLines(logPath);
        Assert.AreEqual(3, lines.Length, "Each call must append one line.");
    }

    // ─────────────────────── Layer 1: file rotation ──────────────────────

    [TestMethod]
    public void FileRotation_PrunesOldEntriesWhenCapExceeded()
    {
        var logPath = Path.Combine(_vaultDir, ".glasswork", "mcp.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        // Write enough lines to exceed 1 MB (each line ~100 bytes → ~11,000 lines).
        var fakeLine = new string('x', 95); // 95 chars + newline ≈ 100 bytes
        var sb = new StringBuilder();
        const int lineCount = 12_000;
        for (int i = 0; i < lineCount; i++)
            sb.AppendLine(fakeLine);
        File.WriteAllText(logPath, sb.ToString());

        Assert.IsTrue(new FileInfo(logPath).Length > McpLogger.MaxLogFileSizeBytes,
            "Pre-condition: file must exceed the cap before rotation.");

        McpLogger.RotateIfNeeded(logPath);

        var remaining = File.ReadAllLines(logPath);
        Assert.IsTrue(remaining.Length < lineCount,
            "After rotation, fewer lines must remain.");
        Assert.IsTrue(remaining.Length >= lineCount / 2 - 1,
            "Roughly the second half of lines should be kept.");
    }

    [TestMethod]
    public void FileEnabled_RotationTriggeredByRealCalls()
    {
        // Pre-populate the log file to just over 1 MB so the next call rotates it.
        var logDir = Path.Combine(_vaultDir, ".glasswork");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "mcp.log");

        var padding = new string('a', 95);
        var sb = new StringBuilder();
        for (int i = 0; i < 12_000; i++)
            sb.AppendLine(padding);
        File.WriteAllText(logPath, sb.ToString());

        var tools = MakeTools(MakeLogger(fileEnabled: true));
        tools.ListTasks(); // triggers rotation + appends new entry

        var sizeAfter = new FileInfo(logPath).Length;
        Assert.IsTrue(sizeAfter < McpLogger.MaxLogFileSizeBytes * 2,
            "File size after rotation must be well below 2x the cap.");
    }

    // ─────────────────────── Layer 2: phase trace ────────────────────────

    [TestMethod]
    public void TraceEnabled_ListTasks_IncludesPhasesObject()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink, traceEnabled: true));

        tools.ListTasks();

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        Assert.IsTrue(doc.RootElement.TryGetProperty("phases", out var phases),
            "With GLASSWORK_MCP_TRACE=1, log line must include 'phases'.");
        Assert.AreEqual(JsonValueKind.Object, phases.ValueKind);
    }

    [TestMethod]
    public void TraceEnabled_ListTasks_PhasesContainExpectedKeys()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink, traceEnabled: true));

        tools.AddTask("Phase Task"); // add something to load
        sink.Clear();
        tools.ListTasks();

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        var phases = doc.RootElement.GetProperty("phases");

        Assert.IsTrue(phases.TryGetProperty("glob", out _), "Must have 'glob' phase.");
        Assert.IsTrue(phases.TryGetProperty("yaml_parse", out _), "Must have 'yaml_parse' phase.");
        Assert.IsTrue(phases.TryGetProperty("filter", out _), "Must have 'filter' phase.");
        Assert.IsTrue(phases.TryGetProperty("sort", out _), "Must have 'sort' phase.");
    }

    [TestMethod]
    public void TraceEnabled_AddTask_IncludesWritePhase()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink, traceEnabled: true));

        tools.AddTask("Write Phase Task");

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        var phases = doc.RootElement.GetProperty("phases");
        Assert.IsTrue(phases.TryGetProperty("write", out _), "add_task must record 'write' phase.");
    }

    [TestMethod]
    public void TraceDisabled_LogLine_NoPhasesObject()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink, traceEnabled: false));

        tools.ListTasks();

        var doc = JsonDocument.Parse(sink.ToString().Trim());
        Assert.IsFalse(doc.RootElement.TryGetProperty("phases", out _),
            "Without GLASSWORK_MCP_TRACE=1, log line must not include 'phases'.");
    }

    [TestMethod]
    public void TraceEnabled_PhaseValues_AreNonNegative()
    {
        var sink = new StringBuilder();
        var tools = MakeTools(MakeLogger(sink, traceEnabled: true));
        tools.AddTask("T");
        sink.Clear();

        tools.ListTasks();

        var phases = JsonDocument.Parse(sink.ToString().Trim()).RootElement.GetProperty("phases");
        foreach (var phase in phases.EnumerateObject())
            Assert.IsTrue(phase.Value.GetInt64() >= 0, $"Phase '{phase.Name}' must be >= 0 ms.");
    }

    // ─────────────────────── helpers ─────────────────────────────────────

    private static bool IsValidJson(string s)
    {
        try { JsonDocument.Parse(s); return true; }
        catch { return false; }
    }
}
