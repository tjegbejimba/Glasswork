using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glasswork.TestInfrastructure;

internal sealed class UnreadableDirectoryScope : IDisposable
{
    private readonly DirectoryInfo _directory;
    private readonly string? _windowsAccessSddl;
    private readonly UnixFileMode? _unixMode;
    private readonly string _sentinelPath;

    private UnreadableDirectoryScope(
        DirectoryInfo directory,
        string? windowsAccessSddl,
        UnixFileMode? unixMode,
        string sentinelPath)
    {
        _directory = directory;
        _windowsAccessSddl = windowsAccessSddl;
        _unixMode = unixMode;
        _sentinelPath = sentinelPath;
    }

    public static UnreadableDirectoryScope Create(string path)
    {
        return Create(path, EnsureUnreadable);
    }

    internal static UnreadableDirectoryScope Create(
        string path,
        Action<string> ensureUnreadable)
    {
        var directory = Directory.CreateDirectory(path);
        return OperatingSystem.IsWindows()
            ? CreateWindows(directory, ensureUnreadable)
            : CreateUnix(directory, ensureUnreadable);
    }

    [SupportedOSPlatform("windows")]
    private static UnreadableDirectoryScope CreateWindows(
        DirectoryInfo directory,
        Action<string> ensureUnreadable)
    {
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var denyRule = new FileSystemAccessRule(
            identity,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        var originalAccessSddl = security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access);
        security.AddAccessRule(denyRule);
        var sentinelPath = CreateSentinel(directory.FullName);

        var succeeded = false;
        try
        {
            directory.SetAccessControl(security);
            ensureUnreadable(directory.FullName);
            succeeded = true;
            return new UnreadableDirectoryScope(
                directory,
                originalAccessSddl,
                null,
                sentinelPath);
        }
        finally
        {
            if (!succeeded)
            {
                RestoreWindows(directory, originalAccessSddl);
                File.Delete(sentinelPath);
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static UnreadableDirectoryScope CreateUnix(
        DirectoryInfo directory,
        Action<string> ensureUnreadable)
    {
        var mode = File.GetUnixFileMode(directory.FullName);
        var sentinelPath = CreateSentinel(directory.FullName);

        var succeeded = false;
        try
        {
            File.SetUnixFileMode(directory.FullName, UnixFileMode.None);
            ensureUnreadable(directory.FullName);
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
        if (OperatingSystem.IsWindows() && _windowsAccessSddl is not null)
        {
            RestoreWindows(_directory, _windowsAccessSddl);
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
        string accessSddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(
            accessSddl,
            AccessControlSections.Access);
        directory.SetAccessControl(security);
    }
}
