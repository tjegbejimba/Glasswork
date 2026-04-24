using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Glasswork.Mcp;

/// <summary>
/// Structured JSONL logger for MCP tool calls.
/// Always writes one JSON line per call to stderr.
/// Optional file sink: <vault>/.glasswork/mcp.log enabled via GLASSWORK_MCP_LOG=1.
/// Optional per-phase timing included via GLASSWORK_MCP_TRACE=1.
/// </summary>
public sealed class McpLogger
{
    public const long MaxLogFileSizeBytes = 1_048_576; // ~1 MB

    private readonly string _vaultPath;
    private readonly TextWriter _stderr;
    private readonly bool _fileEnabled;
    private readonly bool _traceEnabled;

    public McpLogger(VaultContext vaultContext, TextWriter? stderr = null)
        : this(vaultContext.VaultPath, stderr,
               Environment.GetEnvironmentVariable("GLASSWORK_MCP_LOG") == "1",
               Environment.GetEnvironmentVariable("GLASSWORK_MCP_TRACE") == "1")
    { }

    // Constructor used by tests and callers that need to inject a custom stderr sink
    // or override the environment-variable-based feature flags.
    public McpLogger(string vaultPath, TextWriter? stderr, bool fileEnabled, bool traceEnabled)
    {
        _vaultPath = vaultPath;
        _stderr = stderr ?? Console.Error;
        _fileEnabled = fileEnabled;
        _traceEnabled = traceEnabled;
    }

    /// <summary>Whether per-phase tracing is enabled (GLASSWORK_MCP_TRACE=1).</summary>
    public bool IsTracing => _traceEnabled;

    /// <summary>
    /// Begins timing a tool call. Dispose the returned scope to emit the log line.
    /// </summary>
    public CallScope BeginCall(string toolName) => new(this, toolName);

    internal void EmitLogLine(
        string toolName,
        long durationMs,
        string result,
        Dictionary<string, int> counts,
        Dictionary<string, long>? phases)
    {
        var json = BuildJsonLine(toolName, durationMs, result, counts, phases);
        _stderr.WriteLine(json);

        if (_fileEnabled)
            AppendToLogFile(json);
    }

    private static string BuildJsonLine(
        string toolName,
        long durationMs,
        string result,
        Dictionary<string, int> counts,
        Dictionary<string, long>? phases)
    {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("ts", DateTime.UtcNow.ToString("o"));
        writer.WriteString("tool", toolName);
        writer.WriteNumber("duration_ms", durationMs);
        writer.WriteString("result", result);

        foreach (var (key, value) in counts)
            writer.WriteNumber(key, value);

        if (phases is { Count: > 0 })
        {
            writer.WriteStartObject("phases");
            foreach (var (phase, phaseMs) in phases)
                writer.WriteNumber(phase, phaseMs);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void AppendToLogFile(string json)
    {
        var glassworkDir = Path.Combine(_vaultPath, ".glasswork");
        Directory.CreateDirectory(glassworkDir);
        var logPath = Path.Combine(glassworkDir, "mcp.log");

        RotateIfNeeded(logPath);
        File.AppendAllText(logPath, json + Environment.NewLine);
    }

    public static void RotateIfNeeded(string logPath)
    {
        if (!File.Exists(logPath)) return;
        var info = new FileInfo(logPath);
        if (info.Length <= MaxLogFileSizeBytes) return;

        // Prune oldest half of entries to keep the file under the cap.
        var lines = File.ReadAllLines(logPath);
        var keep = lines.Skip(lines.Length / 2).ToArray();
        File.WriteAllLines(logPath, keep);
    }
}

/// <summary>
/// Tracks the timing and result of a single MCP tool call.
/// Dispose to emit the structured JSONL log line.
/// </summary>
public sealed class CallScope : IDisposable
{
    private readonly McpLogger _logger;
    private readonly string _toolName;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Dictionary<string, int> _counts = new();
    private readonly Dictionary<string, long>? _phases;
    private string _result = "ok";
    private bool _disposed;

    internal CallScope(McpLogger logger, string toolName)
    {
        _logger = logger;
        _toolName = toolName;
        _phases = logger.IsTracing ? new Dictionary<string, long>() : null;
    }

    /// <summary>Whether per-phase timing data will be included in the log line.</summary>
    public bool IsTracing => _logger.IsTracing;

    /// <summary>Sets the call outcome: ok | error | conflict | not_found.</summary>
    public void SetResult(string result) => _result = result;

    /// <summary>Records a tool-specific counter (e.g. task_count).</summary>
    public void SetCount(string key, int value) => _counts[key] = value;

    /// <summary>
    /// Records the elapsed time for a named phase. No-op when tracing is disabled.
    /// </summary>
    public void RecordPhase(string name, long milliseconds)
    {
        _phases?.TryAdd(name, milliseconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _total.Stop();
        _logger.EmitLogLine(_toolName, _total.ElapsedMilliseconds, _result, _counts, _phases);
    }
}
