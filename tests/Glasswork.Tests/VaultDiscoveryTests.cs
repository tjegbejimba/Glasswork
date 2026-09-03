using System.Text.Json;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Regression tests for the shared vault-discovery lookup used by every headless
/// Glasswork tool (MCP server, canvas host) so each resolves the same configured
/// Vault independent of GLASSWORK_VAULT being set or the process's cwd (issue #561).
/// </summary>
[TestClass]
public class VaultDiscoveryTests
{
    [TestMethod]
    public void TryDiscover_ResolvesFromEnvironmentVariable_WhenTaskDirectoryExists()
    {
        var tempRoot = CreateVault();
        var originalEnvVar = Environment.GetEnvironmentVariable("GLASSWORK_VAULT");
        try
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", tempRoot);

            var result = VaultDiscovery.TryDiscover(uiStatePathOverride: null, out var diagnostic);

            Assert.AreEqual(Path.GetFullPath(tempRoot), result);
            StringAssert.Contains(diagnostic, "GLASSWORK_VAULT");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", originalEnvVar);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public void TryDiscover_FallsBackToPersistedUiState_WhenEnvVarIsNotSet()
    {
        var tempRoot = CreateVault();
        var uiStatePath = Path.Combine(Path.GetTempPath(), $"ui-state-{Guid.NewGuid()}.json");
        File.WriteAllText(uiStatePath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["vault.path"] = tempRoot,
        }));
        var originalEnvVar = Environment.GetEnvironmentVariable("GLASSWORK_VAULT");
        try
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", null);

            var result = VaultDiscovery.TryDiscover(uiStatePath, out var diagnostic);

            Assert.AreEqual(Path.GetFullPath(tempRoot), result);
            StringAssert.Contains(diagnostic, "app state file");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", originalEnvVar);
            Directory.Delete(tempRoot, recursive: true);
            File.Delete(uiStatePath);
        }
    }

    [TestMethod]
    public void TryDiscover_ReturnsNullWithDiagnostic_WhenNeitherSourceResolves()
    {
        var uiStatePath = Path.Combine(Path.GetTempPath(), $"ui-state-{Guid.NewGuid()}.json");
        var originalEnvVar = Environment.GetEnvironmentVariable("GLASSWORK_VAULT");
        try
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", null);

            var result = VaultDiscovery.TryDiscover(uiStatePath, out var diagnostic);

            Assert.IsNull(result);
            StringAssert.Contains(diagnostic, "could not discover");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", originalEnvVar);
        }
    }

    private static string CreateVault()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vault-discovery-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "wiki", "todo"));
        return tempRoot;
    }
}
