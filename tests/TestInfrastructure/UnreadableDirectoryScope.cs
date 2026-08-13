using System.Security.AccessControl;
using System.Security.Principal;

namespace Glasswork.TestInfrastructure;

internal sealed class UnreadableDirectoryScope : IDisposable
{
    private readonly DirectoryInfo _directory;
    private readonly FileSystemAccessRule? _windowsDenyRule;
    private readonly UnixFileMode? _unixMode;

    private UnreadableDirectoryScope(
        DirectoryInfo directory,
        FileSystemAccessRule? windowsDenyRule,
        UnixFileMode? unixMode)
    {
        _directory = directory;
        _windowsDenyRule = windowsDenyRule;
        _unixMode = unixMode;
    }

    public static UnreadableDirectoryScope Create(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows user has no SID.");
            var denyRule = new FileSystemAccessRule(
                identity,
                FileSystemRights.ListDirectory,
                AccessControlType.Deny);
            var security = directory.GetAccessControl();
            security.AddAccessRule(denyRule);
            directory.SetAccessControl(security);
            return new UnreadableDirectoryScope(directory, denyRule, null);
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new UnreadableDirectoryScope(directory, null, mode);
    }

    public void Dispose()
    {
        if (_windowsDenyRule is not null)
        {
            var security = _directory.GetAccessControl();
            security.RemoveAccessRuleSpecific(_windowsDenyRule);
            _directory.SetAccessControl(security);
        }
        else if (_unixMode.HasValue)
        {
            File.SetUnixFileMode(_directory.FullName, _unixMode.Value);
        }
    }
}
