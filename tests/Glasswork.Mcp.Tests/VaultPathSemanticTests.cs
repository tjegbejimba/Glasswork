using System.IO;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

/// <summary>
/// Regression tests for issue #132: ensure vault.path means "vault root"
/// consistently across App and MCP, and that both resolve to the same
/// on-disk task directory.
/// </summary>
[TestClass]
public class VaultPathSemanticTests
{
    [TestMethod]
    public void GlassworkTools_ResolveTaskDirectory_FromVaultRoot()
    {
        // Arrange: create a temp vault with wiki/todo structure
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vault-test-{Guid.NewGuid()}");
        var todoPath = Path.Combine(tempRoot, "wiki", "todo");
        Directory.CreateDirectory(todoPath);

        try
        {
            // Act: construct GlassworkTools with vault root (matching GLASSWORK_VAULT semantic)
            var vaultContext = new VaultContext(tempRoot);
            var tools = new GlassworkTools(vaultContext);

            // Create a task and verify it lands in wiki/todo
            var result = tools.AddTask(title: "Test task");
            
            // Parse the JSON result to get the task ID
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            var taskId = doc.RootElement.GetProperty("task_id").GetString()!;
            
            // Assert: task file should exist in <root>/wiki/todo, not at the root
            var expectedPath = Path.Combine(todoPath, $"{taskId}.md");
            Assert.IsTrue(File.Exists(expectedPath), 
                $"Expected task file at {expectedPath}, but it does not exist");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public void VaultDiscovery_Discover_FailsWhenTaskDirectoryMissing()
    {
        // Arrange: vault root exists but wiki/todo does not
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vault-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempRoot);
        // Deliberately NOT creating wiki/todo subdirectory

        // Set up environment to point to this vault
        var originalEnvVar = Environment.GetEnvironmentVariable("GLASSWORK_VAULT");
        try
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", tempRoot);
            
            // Act: TryDiscover should return null when task directory doesn't exist
            var result = VaultDiscovery.TryDiscover(out var diagnostic);
            
            // Assert: should fail and diagnostic should mention wiki/todo
            Assert.IsNull(result, "Expected TryDiscover to return null when task directory is missing");
            Assert.IsTrue(diagnostic.Contains("wiki") && diagnostic.Contains("todo"), 
                $"Expected diagnostic to mention wiki/todo, but got: {diagnostic}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLASSWORK_VAULT", originalEnvVar);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
