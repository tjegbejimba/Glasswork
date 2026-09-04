using Glasswork.TestInfrastructure;

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
}
