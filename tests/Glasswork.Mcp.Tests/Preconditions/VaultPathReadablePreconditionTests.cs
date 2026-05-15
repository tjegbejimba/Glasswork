using Glasswork.Mcp;
using Glasswork.Mcp.Preconditions;

namespace Glasswork.Mcp.Tests.Preconditions;

[TestClass]
public sealed class VaultPathReadablePreconditionTests
{
    [TestMethod]
    public void Evaluate_returns_Unavailable_when_path_is_null()
    {
        var precondition = new VaultPathReadablePrecondition(new VaultContext(null));

        var result = precondition.Evaluate();

        Assert.IsFalse(result.IsOk);
        Assert.IsTrue(!string.IsNullOrWhiteSpace(result.Reason));
    }

    [TestMethod]
    public void Evaluate_returns_Unavailable_when_directory_missing()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "glasswork-missing-" + Guid.NewGuid().ToString("N"));
        var precondition = new VaultPathReadablePrecondition(new VaultContext(bogus));

        var result = precondition.Evaluate();

        Assert.IsFalse(result.IsOk);
        StringAssert.Contains(result.Reason ?? string.Empty, bogus);
    }

    [TestMethod]
    public void Evaluate_returns_Ok_when_directory_readable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glasswork-readable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var precondition = new VaultPathReadablePrecondition(new VaultContext(dir));

            var result = precondition.Evaluate();

            Assert.IsTrue(result.IsOk, result.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Name_is_stable_identifier()
    {
        var precondition = new VaultPathReadablePrecondition(new VaultContext(null));
        Assert.AreEqual("vault-path-readable", precondition.Name);
    }
}
