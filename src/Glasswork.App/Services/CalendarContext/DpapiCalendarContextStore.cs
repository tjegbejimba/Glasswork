using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glasswork.Core.CalendarContext;

namespace Glasswork.Services.CalendarContext;

public sealed class DpapiCalendarContextStore : ICalendarContextStore
{
    private const int OuterSchemaVersion = 1;
    private const int ConfigurationSchemaVersion = 1;
    private const int SnapshotSchemaVersion = 1;
    private const int NormalizationVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _configurationPath;
    private readonly string _snapshotPath;

    public DpapiCalendarContextStore(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _configurationPath = Path.Combine(baseDirectory, "configuration.json");
        _snapshotPath = Path.Combine(baseDirectory, "snapshot.json");
    }

    public static DpapiCalendarContextStore CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Glasswork",
            "calendar-context"));

    public CalendarContextStoreRead<CalendarContextConfiguration> ReadConfiguration() =>
        ReadProtected<CalendarContextConfiguration>(
            _configurationPath,
            "configuration",
            configuration => configuration.SchemaVersion,
            ConfigurationSchemaVersion);

    public CalendarContextStoreRead<CalendarContextSnapshot> ReadSnapshot()
    {
        var result = ReadProtected<CalendarContextSnapshot>(
            _snapshotPath,
            "snapshot",
            snapshot => snapshot.SchemaVersion,
            SnapshotSchemaVersion);
        if (result is { Status: CalendarContextStoreStatus.Ready, Value: { } snapshot }
            && snapshot.NormalizationVersion != NormalizationVersion)
        {
            return snapshot.NormalizationVersion > NormalizationVersion
                ? CalendarContextStoreRead<CalendarContextSnapshot>.UnsupportedVersion()
                : CalendarContextStoreRead<CalendarContextSnapshot>.Corrupt();
        }

        return result;
    }

    public void WriteConfiguration(CalendarContextConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        WriteProtected(_configurationPath, "configuration", configuration);
    }

    public void WriteSnapshot(CalendarContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        WriteProtected(_snapshotPath, "snapshot", snapshot);
    }

    public void DeleteConfiguration() => DeleteIfPresent(_configurationPath);

    public void DeleteSnapshot() => DeleteIfPresent(_snapshotPath);

    private static CalendarContextStoreRead<T> ReadProtected<T>(
        string path,
        string purpose,
        Func<T, int> innerVersion,
        int supportedInnerVersion)
        where T : class
    {
        if (!File.Exists(path))
            return CalendarContextStoreRead<T>.Missing();

        try
        {
            var outer = JsonSerializer.Deserialize<ProtectedEnvelope>(
                File.ReadAllBytes(path),
                JsonOptions);
            if (outer is null
                || outer.SchemaVersion < OuterSchemaVersion
                || string.IsNullOrWhiteSpace(outer.ProtectedPayload))
            {
                return CalendarContextStoreRead<T>.Corrupt();
            }

            if (outer.SchemaVersion > OuterSchemaVersion)
                return CalendarContextStoreRead<T>.UnsupportedVersion();

            byte[] protectedPayload;
            try
            {
                protectedPayload = Convert.FromBase64String(outer.ProtectedPayload);
            }
            catch (FormatException)
            {
                return CalendarContextStoreRead<T>.Corrupt();
            }

            byte[] plaintext;
            try
            {
                plaintext = ProtectedData.Unprotect(
                    protectedPayload,
                    Entropy(purpose),
                    DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return CalendarContextStoreRead<T>.Undecryptable();
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(plaintext, JsonOptions);
                if (value is null || innerVersion(value) < supportedInnerVersion)
                    return CalendarContextStoreRead<T>.Corrupt();
                if (innerVersion(value) > supportedInnerVersion)
                    return CalendarContextStoreRead<T>.UnsupportedVersion();
                return CalendarContextStoreRead<T>.Ready(value);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (JsonException)
        {
            return CalendarContextStoreRead<T>.Corrupt();
        }
        catch (IOException)
        {
            return CalendarContextStoreRead<T>.Corrupt();
        }
        catch (UnauthorizedAccessException)
        {
            return CalendarContextStoreRead<T>.Corrupt();
        }
    }

    private static void WriteProtected<T>(string path, string purpose, T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[]? protectedPayload = null;
        try
        {
            protectedPayload = ProtectedData.Protect(
                plaintext,
                Entropy(purpose),
                DataProtectionScope.CurrentUser);
            var envelope = new ProtectedEnvelope(
                OuterSchemaVersion,
                Convert.ToBase64String(protectedPayload));
            AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedPayload is not null)
                CryptographicOperations.ZeroMemory(protectedPayload);
        }
    }

    private static void AtomicWrite(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Calendar Context store path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static byte[] Entropy(string purpose) =>
        Encoding.UTF8.GetBytes($"Glasswork.CalendarContext.{purpose}.v1");

    private sealed record ProtectedEnvelope(int SchemaVersion, string ProtectedPayload);
}
