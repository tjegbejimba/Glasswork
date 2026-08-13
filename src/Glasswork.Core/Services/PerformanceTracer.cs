using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Glasswork.Core.Services;

public interface IPerformanceTraceScope : IDisposable
{
    void SetCount(string name, int value);
    void SetTag(string name, string value);
    void SetTag(string name, bool value);
    void SetOutcome(string outcome);
    void Cancel();
}

public interface IPerformanceTracer : IDisposable
{
    bool IsEnabled { get; }
    string? OutputPath { get; }
    IPerformanceTraceScope BeginSpan(string eventName);
    void EmitMilestone(string eventName);
}

public static class PerformanceTracer
{
    public const string EnabledVariable = "GLASSWORK_PERF_TRACE";
    public const string PathVariable = "GLASSWORK_PERF_TRACE_PATH";

    public static IPerformanceTracer Disabled => DisabledPerformanceTracer.Instance;

    public static IPerformanceTracer CreateFromProcessEnvironment(long baselineTimestamp)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnabledVariable),
                "1",
                StringComparison.Ordinal))
        {
            return Disabled;
        }

        return CreateFromEnvironment(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [EnabledVariable] = "1",
                [PathVariable] = Environment.GetEnvironmentVariable(PathVariable),
            },
            baselineTimestamp);
    }

    public static IPerformanceTracer CreateFromEnvironment(
        IReadOnlyDictionary<string, string?> environment,
        long baselineTimestamp)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.Equals(Read(environment, EnabledVariable), "1", StringComparison.Ordinal))
            return Disabled;

        try
        {
            var configuredPath = Read(environment, PathVariable);
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(
                    Path.GetTempPath(),
                    $"glasswork-perf-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.jsonl")
                : Path.GetFullPath(configuredPath);
            return new JsonlPerformanceTracer(path, baselineTimestamp);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"Performance trace disabled: {ex.Message}");
            return Disabled;
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"Performance trace disabled: {ex.Message}");
            return Disabled;
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Performance trace disabled: {ex.Message}");
            return Disabled;
        }
        catch (NotSupportedException ex)
        {
            Debug.WriteLine($"Performance trace disabled: {ex.Message}");
            return Disabled;
        }
    }

    private static string? Read(IReadOnlyDictionary<string, string?> environment, string name)
    {
        if (environment.TryGetValue(name, out var value))
            return value;

        foreach (var pair in environment)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private sealed class JsonlPerformanceTracer : IPerformanceTracer
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer;
        private readonly long _baselineTimestamp;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private bool _disposed;

        public JsonlPerformanceTracer(string path, long baselineTimestamp)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            _baselineTimestamp = baselineTimestamp;
            OutputPath = path;
            Debug.WriteLine($"Performance trace enabled: {path}");
        }

        public bool IsEnabled
        {
            get { lock (_gate) { return !_disposed; } }
        }
        public string OutputPath { get; }

        public IPerformanceTraceScope BeginSpan(string eventName)
        {
            lock (_gate)
            {
                if (_disposed)
                    return DisabledPerformanceTraceScope.Instance;
            }

            return new PerformanceTraceScope(this, ValidateEventName(eventName));
        }

        public void EmitMilestone(string eventName)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
            }

            Emit(
                ValidateEventName(eventName),
                "milestone",
                durationMs: null,
                "ok",
                new Dictionary<string, object>(StringComparer.Ordinal));
        }

        internal void Emit(
            string eventName,
            string kind,
            long? durationMs,
            string outcome,
            IReadOnlyDictionary<string, object> metadata)
        {
            var json = BuildJsonLine(eventName, kind, durationMs, outcome, metadata);
            Debug.WriteLine(json);

            lock (_gate)
            {
                if (_disposed)
                    return;

                try
                {
                    _writer.WriteLine(json);
                }
                catch (IOException ex)
                {
                    Debug.WriteLine($"Performance trace write failed: {ex.Message}");
                }
                catch (ObjectDisposedException ex)
                {
                    Debug.WriteLine($"Performance trace write failed: {ex.Message}");
                }
            }
        }

        private string BuildJsonLine(
            string eventName,
            string kind,
            long? durationMs,
            string outcome,
            IReadOnlyDictionary<string, object> metadata)
        {
            using var buffer = new MemoryStream();
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            writer.WriteString("ts", DateTime.UtcNow.ToString("O"));
            writer.WriteString("session_id", _sessionId);
            writer.WriteString("event", eventName);
            writer.WriteString("kind", kind);
            if (durationMs.HasValue)
                writer.WriteNumber("duration_ms", durationMs.Value);
            writer.WriteNumber(
                "elapsed_ms",
                ElapsedMilliseconds(_baselineTimestamp, Stopwatch.GetTimestamp()));
            writer.WriteNumber("thread_id", Environment.CurrentManagedThreadId);
            writer.WriteString("outcome", outcome);

            foreach (var (name, value) in metadata)
            {
                switch (value)
                {
                    case int count:
                        writer.WriteNumber(name, count);
                        break;
                    case string text:
                        writer.WriteString(name, text);
                        break;
                    case bool flag:
                        writer.WriteBoolean(name, flag);
                        break;
                }
            }

            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _writer.Dispose();
            }
        }

        private static string ValidateEventName(string eventName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
            return eventName;
        }
    }

    private sealed class PerformanceTraceScope : IPerformanceTraceScope
    {
        private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
        {
            "ts",
            "session_id",
            "event",
            "kind",
            "duration_ms",
            "elapsed_ms",
            "thread_id",
            "outcome",
        };

        private readonly JsonlPerformanceTracer _tracer;
        private readonly string _eventName;
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
        private readonly Dictionary<string, object> _metadata = new(StringComparer.Ordinal);
        private string _outcome = "ok";
        private bool _cancelled;
        private bool _disposed;

        public PerformanceTraceScope(JsonlPerformanceTracer tracer, string eventName)
        {
            _tracer = tracer;
            _eventName = eventName;
        }

        public void SetCount(string name, int value) => _metadata[ValidateMetadataName(name)] = value;
        public void SetTag(string name, string value) => _metadata[ValidateMetadataName(name)] = value;
        public void SetTag(string name, bool value) => _metadata[ValidateMetadataName(name)] = value;
        public void SetOutcome(string outcome) => _outcome = outcome;
        public void Cancel() => _cancelled = true;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_cancelled)
                return;

            var stoppedTimestamp = Stopwatch.GetTimestamp();
            _tracer.Emit(
                _eventName,
                "span",
                ElapsedMilliseconds(_startedTimestamp, stoppedTimestamp),
                _outcome,
                _metadata);
        }

        private static string ValidateMetadataName(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (ReservedNames.Contains(name))
                throw new ArgumentException($"'{name}' is reserved by the performance trace envelope.", nameof(name));
            return name;
        }
    }

    private sealed class DisabledPerformanceTracer : IPerformanceTracer
    {
        public static DisabledPerformanceTracer Instance { get; } = new();

        public bool IsEnabled => false;
        public string? OutputPath => null;
        public IPerformanceTraceScope BeginSpan(string eventName) => DisabledPerformanceTraceScope.Instance;
        public void EmitMilestone(string eventName) { }
        public void Dispose() { }
    }

    private sealed class DisabledPerformanceTraceScope : IPerformanceTraceScope
    {
        public static DisabledPerformanceTraceScope Instance { get; } = new();

        public void SetCount(string name, int value) { }
        public void SetTag(string name, string value) { }
        public void SetTag(string name, bool value) { }
        public void SetOutcome(string outcome) { }
        public void Cancel() { }
        public void Dispose() { }
    }

    private static long ElapsedMilliseconds(long startTimestamp, long endTimestamp) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMilliseconds);
}
