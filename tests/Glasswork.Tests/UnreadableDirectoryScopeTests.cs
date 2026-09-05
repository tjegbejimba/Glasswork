using Glasswork.TestInfrastructure;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glasswork.Tests;

[TestClass]
public sealed class UnreadableDirectoryScopeTests
{
    [TestMethod]
    public void Create_MakesDirectoryUnreadableUntilDisposed()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"glasswork-unreadable-scope-{Guid.NewGuid():N}");
        var sentinel = Path.Combine(root, "sentinel.txt");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(sentinel, "sentinel");

            using (UnreadableDirectoryScope.Create(root))
            {
                Assert.ThrowsExactly<UnauthorizedAccessException>(
                    () => Directory.GetFileSystemEntries(root));
            }

            CollectionAssert.Contains(
                Directory.GetFileSystemEntries(root),
                sentinel);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Dispose_PreservesPreExistingIdenticalDenyRule()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows ACL semantics are covered only on Windows.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"glasswork-unreadable-scope-existing-deny-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(root);
        var originalSecurity = directory.GetAccessControl(
            AccessControlSections.Access);
        var originalSddl = originalSecurity.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access);
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var denyRule = new FileSystemAccessRule(
            identity,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);

        try
        {
            var restrictedSecurity = directory.GetAccessControl(
                AccessControlSections.Access);
            restrictedSecurity.AddAccessRule(denyRule);
            directory.SetAccessControl(restrictedSecurity);
            var expectedRestrictedSddl = directory
                .GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            using (UnreadableDirectoryScope.Create(root))
            {
                Assert.ThrowsExactly<UnauthorizedAccessException>(
                    () => Directory.GetFileSystemEntries(root));
            }

            var actualRestrictedSddl = directory
                .GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.AreEqual(expectedRestrictedSddl, actualRestrictedSddl);
            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => Directory.GetFileSystemEntries(root));
        }
        finally
        {
            var restoredSecurity = new DirectorySecurity();
            restoredSecurity.SetSecurityDescriptorSddlForm(
                originalSddl,
                AccessControlSections.Access);
            directory.SetAccessControl(restoredSecurity);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Create_WhenVerificationFails_RestoresPreExistingAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows ACL semantics are covered only on Windows.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"glasswork-unreadable-scope-failed-create-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(root);
        var originalSecurity = directory.GetAccessControl(
            AccessControlSections.Access);
        var originalSddl = originalSecurity.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access);
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var denyRule = new FileSystemAccessRule(
            identity,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);

        try
        {
            var restrictedSecurity = directory.GetAccessControl(
                AccessControlSections.Access);
            restrictedSecurity.AddAccessRule(denyRule);
            directory.SetAccessControl(restrictedSecurity);
            var expectedRestrictedSddl = directory
                .GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            Assert.ThrowsExactly<InvalidOperationException>(
                () => UnreadableDirectoryScope.Create(
                    root,
                    _ => throw new InvalidOperationException("verification failed")));

            var actualRestrictedSddl = directory
                .GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.AreEqual(expectedRestrictedSddl, actualRestrictedSddl);
            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => Directory.GetFileSystemEntries(root));
        }
        finally
        {
            var restoredSecurity = new DirectorySecurity();
            restoredSecurity.SetSecurityDescriptorSddlForm(
                originalSddl,
                AccessControlSections.Access);
            directory.SetAccessControl(restoredSecurity);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
