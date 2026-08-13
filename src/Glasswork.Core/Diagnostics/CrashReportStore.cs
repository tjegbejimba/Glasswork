using System.Text;

namespace Glasswork.Core.Diagnostics;

public sealed record CrashReportContext(
    string AppVersion,
    string OsDescription,
    string RuntimeDescription);

public sealed class CrashReportStore
{
    private readonly object _gate = new();
    private readonly string _directoryPath;
    private readonly int _maxReports;

    public CrashReportStore(string directoryPath, int maxReports = 10)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Crash report directory must not be empty.", nameof(directoryPath));
        if (maxReports < 1)
            throw new ArgumentOutOfRangeException(nameof(maxReports), "At least one crash report must be retained.");

        _directoryPath = directoryPath;
        _maxReports = maxReports;
    }

    public string Record(string source, Exception exception, CrashReportContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            Directory.CreateDirectory(_directoryPath);
            var timestamp = DateTimeOffset.UtcNow;
            var path = Path.Combine(
                _directoryPath,
                $"crash-{timestamp:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.log");

            var report = new StringBuilder()
                .AppendLine($"Timestamp (UTC): {timestamp:O}")
                .AppendLine($"Source: {source}")
                .AppendLine($"App version: {context.AppVersion}")
                .AppendLine($"OS: {context.OsDescription}")
                .AppendLine($"Runtime: {context.RuntimeDescription}")
                .AppendLine()
                .AppendLine(exception.ToString())
                .ToString();

            File.WriteAllText(path, report, Encoding.UTF8);
            PruneOldReports();
            return path;
        }
    }

    private void PruneOldReports()
    {
        var staleReports = new DirectoryInfo(_directoryPath)
            .EnumerateFiles("crash-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_maxReports);

        foreach (var staleReport in staleReports)
            staleReport.Delete();
    }
}
