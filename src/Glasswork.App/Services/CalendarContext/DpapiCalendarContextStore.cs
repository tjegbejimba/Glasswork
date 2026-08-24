using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glasswork.Core.CalendarContext;

namespace Glasswork.Services.CalendarContext;

public sealed partial class DpapiCalendarContextStore : ICalendarContextStore
{
    private const int OuterSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _configurationPath;
    private readonly string _snapshotPath;
    private readonly ICalendarDataProtector _protector;

    public DpapiCalendarContextStore(
        string baseDirectory,
        ICalendarDataProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _configurationPath = Path.Combine(baseDirectory, "configuration.json");
        _snapshotPath = Path.Combine(baseDirectory, "snapshot.json");
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public CalendarContextStoreRead<CalendarContextConfiguration> ReadConfiguration() =>
        ReadProtected<CalendarContextConfiguration>(
            _configurationPath,
            "configuration",
            configuration => configuration.SchemaVersion,
            CalendarContextPersistenceContract.ConfigurationSchemaVersion,
            CalendarContextPersistenceContract.IsConfigurationValid,
            olderVersionIsMissing: false);

    public CalendarContextStoreRead<CalendarContextSnapshot> ReadSnapshot()
    {
        var result = ReadProtected<CalendarContextSnapshot>(
            _snapshotPath,
            "snapshot",
            snapshot => snapshot.SchemaVersion,
            CalendarContextPersistenceContract.SnapshotSchemaVersion,
            CalendarContextPersistenceContract.IsSnapshotValid,
            olderVersionIsMissing: true);
        if (result is { Status: CalendarContextStoreStatus.Ready, Value: { } snapshot })
        {
            if (snapshot.NormalizationVersion < 0)
                return CalendarContextStoreRead<CalendarContextSnapshot>.Corrupt();
            if (snapshot.NormalizationVersion >
                CalendarContextPersistenceContract.NormalizationVersion)
            {
                return CalendarContextStoreRead<CalendarContextSnapshot>.UnsupportedVersion();
            }
            if (snapshot.NormalizationVersion <
                CalendarContextPersistenceContract.NormalizationVersion)
            {
                return CalendarContextStoreRead<CalendarContextSnapshot>.Missing();
            }
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

    private CalendarContextStoreRead<T> ReadProtected<T>(
        string path,
        string purpose,
        Func<T, int> innerVersion,
        int supportedInnerVersion,
        Func<T, bool> validate,
        bool olderVersionIsMissing)
        where T : class
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return CalendarContextStoreRead<T>.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return CalendarContextStoreRead<T>.Missing();
        }
        catch (IOException)
        {
            return CalendarContextStoreRead<T>.TransientFailure();
        }
        catch (UnauthorizedAccessException)
        {
            return CalendarContextStoreRead<T>.TransientFailure();
        }

        try
        {
            var outer = JsonSerializer.Deserialize<ProtectedEnvelope>(
                content,
                JsonOptions);
            if (outer is null
                || outer.SchemaVersion < 0
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
                plaintext = _protector.Unprotect(protectedPayload, Entropy(purpose));
            }
            catch (CryptographicException)
            {
                return CalendarContextStoreRead<T>.Undecryptable();
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(plaintext, JsonOptions);
                if (value is null)
                    return CalendarContextStoreRead<T>.Corrupt();
                var version = innerVersion(value);
                if (version < 0)
                    return CalendarContextStoreRead<T>.Corrupt();
                if (version > supportedInnerVersion)
                    return CalendarContextStoreRead<T>.UnsupportedVersion();
                if (version < supportedInnerVersion && olderVersionIsMissing)
                    return CalendarContextStoreRead<T>.Missing();
                if (!validate(value))
                    return CalendarContextStoreRead<T>.Corrupt();
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
    }

    private void WriteProtected<T>(string path, string purpose, T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[]? protectedPayload = null;
        try
        {
            protectedPayload = _protector.Protect(plaintext, Entropy(purpose));
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
