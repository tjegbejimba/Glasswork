using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glasswork.TestInfrastructure;

internal sealed class UnreadableDirectoryScope : IDisposable
{
    private readonly DirectoryInfo _directory;
    private readonly FileSystemAccessRule? _windowsDenyRule;
    private readonly UnixFileMode? _unixMode;
    private readonly string _sentinelPath;

    private UnreadableDirectoryScope(
        DirectoryInfo directory,
        FileSystemAccessRule? windowsDenyRule,
        UnixFileMode? unixMode,
        string sentinelPath)
    {
        _directory = directory;
        _windowsDenyRule = windowsDenyRule;
        _unixMode = unixMode;
        _sentinelPath = sentinelPath;
    }

    public static UnreadableDirectoryScope Create(string path)
    {
        var directory = Directory.CreateDirectory(path);
        return OperatingSystem.IsWindows()
            ? CreateWindows(directory)
            : CreateUnix(directory);
    }

    [SupportedOSPlatform("windows")]
    private static UnreadableDirectoryScope CreateWindows(DirectoryInfo directory)
    {
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var denyRule = new FileSystemAccessRule(
            identity,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(denyRule);
        var sentinelPath = CreateSentinel(directory.FullName);

        var succeeded = false;
        try
        {
            directory.SetAccessControl(security);
            EnsureUnreadable(directory.FullName);
            succeeded = true;
            return new UnreadableDirectoryScope(
                directory,
                denyRule,
                null,
                sentinelPath);
        }
        finally
        {
            if (!succeeded)
            {
                RestoreWindows(directory, denyRule);
                File.Delete(sentinelPath);
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static UnreadableDirectoryScope CreateUnix(DirectoryInfo directory)
    {
        var mode = File.GetUnixFileMode(directory.FullName);
        var sentinelPath = CreateSentinel(directory.FullName);

        var succeeded = false;
        try
        {
            File.SetUnixFileMode(directory.FullName, UnixFileMode.None);
            EnsureUnreadable(directory.FullName);
            succeeded = true;
            return new UnreadableDirectoryScope(
                directory,
                null,
                mode,
                sentinelPath);
        }
        finally
        {
            if (!succeeded)
            {
                File.SetUnixFileMode(directory.FullName, mode);
                File.Delete(sentinelPath);
            }
        }
    }

    private static string CreateSentinel(string path)
    {
        var sentinelPath = Path.Combine(
            path,
            $".glasswork-unreadable-{Guid.NewGuid():N}");
        File.WriteAllText(sentinelPath, "permission probe");
        return sentinelPath;
    }

    private static void EnsureUnreadable(string path)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(path);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The current process can still enumerate '{path}' after access was removed.");
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && _windowsDenyRule is not null)
        {
            RestoreWindows(_directory, _windowsDenyRule);
        }
        else if (!OperatingSystem.IsWindows() && _unixMode.HasValue)
        {
            File.SetUnixFileMode(_directory.FullName, _unixMode.Value);
        }

        File.Delete(_sentinelPath);
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreWindows(
        DirectoryInfo directory,
        FileSystemAccessRule denyRule)
    {
        var security = directory.GetAccessControl();
        security.RemoveAccessRuleSpecific(denyRule);
        directory.SetAccessControl(security);
    }
}
