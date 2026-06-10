using System.Text.Json;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

/// <summary>
/// Tests for multi-format artifact support in MCP tools (Slice 4 of PRD #318).
/// Verifies LoadArtifactsWithBodies inlines only Markdown/Text under cap,
/// and add_artifact accepts text extensions with atomic write.
/// </summary>
[TestClass]
public class MultiFormatArtifactTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");
    
    private string ResolveTodoPath(string todoRelativePath) =>
        Path.Combine(TasksDir, todoRelativePath.Replace('/', Path.DirectorySeparatorChar));

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-multiformat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    // ───────────────────── LoadArtifactsWithBodies (reading) ─────────────────────

    [TestMethod]
    public void LoadArtifacts_MarkdownUnderCap_InlinesContent()
    {
        // Arrange: Create a task and add a markdown artifact
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        var artifactJson = _tools.AddArtifact(taskId, "plan.md", "# Plan\nThis is the plan.");
        
        // Act: Load the task with artifacts (including bodies)
        var getTaskJson = _tools.GetTask(taskId, include_artifact_bodies: true);
        var doc = JsonDocument.Parse(getTaskJson);
        var artifacts = doc.RootElement.GetProperty("artifacts");
        
        // Assert: Markdown artifact should have inlined content
        Assert.AreEqual(1, artifacts.GetArrayLength(), "Should have one artifact");
        
        var artifact = artifacts[0];
        Assert.AreEqual("plan.md", artifact.GetProperty("filename").GetString());
        Assert.IsTrue(artifact.TryGetProperty("content", out var content), "Should have content property");
        Assert.AreEqual("# Plan\nThis is the plan.", content.GetString());
        
        // Additive fields
        Assert.IsTrue(artifact.TryGetProperty("kind", out var kind), "Should have kind property");
        Assert.AreEqual("Markdown", kind.GetString());
        Assert.IsTrue(artifact.TryGetProperty("inline", out var inline), "Should have inline property");
        Assert.IsTrue(inline.GetBoolean(), "Should be inline");
    }

    [TestMethod]
    public void LoadArtifacts_TextUnderCap_InlinesContent()
    {
        // Arrange: Create a task with a text artifact
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        // Directly write a .txt file (AddArtifact only accepts .md currently, we'll update it later)
        var artifactFolder = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "notes.txt"), "Plain text notes.");
        
        // Act: Load the task with artifacts
        var getTaskJson = _tools.GetTask(taskId, include_artifact_bodies: true);
        var doc = JsonDocument.Parse(getTaskJson);
        var artifacts = doc.RootElement.GetProperty("artifacts");
        
        // Assert: Text artifact should have inlined content
        Assert.AreEqual(1, artifacts.GetArrayLength(), "Should have one artifact");
        
        var artifact = artifacts[0];
        Assert.AreEqual("notes.txt", artifact.GetProperty("filename").GetString());
        Assert.IsTrue(artifact.TryGetProperty("content", out var content), "Should have content property");
        Assert.AreEqual("Plain text notes.", content.GetString());
        
        // Additive fields
        Assert.IsTrue(artifact.TryGetProperty("kind", out var kind), "Should have kind property");
        Assert.AreEqual("Text", kind.GetString());
        Assert.IsTrue(artifact.TryGetProperty("inline", out var inline), "Should have inline property");
        Assert.IsTrue(inline.GetBoolean(), "Should be inline");
    }

    [TestMethod]
    public void LoadArtifacts_TextOverCap_ByReference()
    {
        // Arrange: Create a task with over-cap text artifact
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        // Write a file > 256KB
        var artifactFolder = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts");
        Directory.CreateDirectory(artifactFolder);
        var largeText = new string('x', 256 * 1024 + 1); // Just over cap
        File.WriteAllText(Path.Combine(artifactFolder, "large.txt"), largeText);
        
        // Act
        var getTaskJson = _tools.GetTask(taskId, include_artifact_bodies: true);
        var doc = JsonDocument.Parse(getTaskJson);
        var artifacts = doc.RootElement.GetProperty("artifacts");
        
        // Assert: Should be by-reference with reason "over_cap"
        Assert.AreEqual(1, artifacts.GetArrayLength());
        var artifact = artifacts[0];
        Assert.AreEqual("large.txt", artifact.GetProperty("filename").GetString());
        Assert.IsFalse(artifact.TryGetProperty("content", out _), "Should NOT have content");
        Assert.IsTrue(artifact.TryGetProperty("inline", out var inline), "Should have inline property");
        Assert.IsFalse(inline.GetBoolean(), "Should NOT be inline");
        Assert.IsTrue(artifact.TryGetProperty("reason", out var reason), "Should have reason");
        Assert.AreEqual("over_cap", reason.GetString());
        Assert.IsTrue(artifact.TryGetProperty("kind", out var kind), "Should have kind");
        Assert.AreEqual("Text", kind.GetString());
    }

    [TestMethod]
    public void LoadArtifacts_HtmlFile_ByReference()
    {
        // Arrange: Create task with HTML artifact
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        var artifactFolder = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts");
        Directory.CreateDirectory(artifactFolder);
        File.WriteAllText(Path.Combine(artifactFolder, "report.html"), "<html><body>Report</body></html>");
        
        // Act
        var getTaskJson = _tools.GetTask(taskId, include_artifact_bodies: true);
        var doc = JsonDocument.Parse(getTaskJson);
        var artifacts = doc.RootElement.GetProperty("artifacts");
        
        // Assert: HTML is by-reference with reason "binary"
        Assert.AreEqual(1, artifacts.GetArrayLength());
        var artifact = artifacts[0];
        Assert.AreEqual("report.html", artifact.GetProperty("filename").GetString());
        Assert.IsFalse(artifact.TryGetProperty("content", out _), "Should NOT have content");
        Assert.IsTrue(artifact.TryGetProperty("inline", out var inline), "Should have inline");
        Assert.IsFalse(inline.GetBoolean(), "Should NOT be inline");
        Assert.IsTrue(artifact.TryGetProperty("reason", out var reason), "Should have reason");
        Assert.AreEqual("binary", reason.GetString());
        Assert.IsTrue(artifact.TryGetProperty("kind", out var kind), "Should have kind");
        Assert.AreEqual("Html", kind.GetString());
    }

    [TestMethod]
    public void LoadArtifacts_ImageFile_ByReference()
    {
        // Arrange: Create task with image artifact
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        var artifactFolder = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts");
        Directory.CreateDirectory(artifactFolder);
        // Write a fake PNG (just bytes, doesn't have to be valid)
        File.WriteAllBytes(Path.Combine(artifactFolder, "diagram.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        
        // Act
        var getTaskJson = _tools.GetTask(taskId, include_artifact_bodies: true);
        var doc = JsonDocument.Parse(getTaskJson);
        var artifacts = doc.RootElement.GetProperty("artifacts");
        
        // Assert: Image is by-reference with reason "binary"
        Assert.AreEqual(1, artifacts.GetArrayLength());
        var artifact = artifacts[0];
        Assert.AreEqual("diagram.png", artifact.GetProperty("filename").GetString());
        Assert.IsFalse(artifact.TryGetProperty("content", out _), "Should NOT have content");
        Assert.IsTrue(artifact.TryGetProperty("inline", out var inline), "Should have inline");
        Assert.IsFalse(inline.GetBoolean(), "Should NOT be inline");
        Assert.IsTrue(artifact.TryGetProperty("reason", out var reason), "Should have reason");
        Assert.AreEqual("binary", reason.GetString());
        Assert.IsTrue(artifact.TryGetProperty("kind", out var kind), "Should have kind");
        Assert.AreEqual("Image", kind.GetString());
    }

    // ───────────────────── AddArtifact (writing) ─────────────────────

    [TestMethod]
    public void AddArtifact_TextExtension_Succeeds()
    {
        // Arrange
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        // Act: AddArtifact with .txt extension (will be implemented)
        var artifactJson = _tools.AddArtifact(taskId, "notes.txt", "Plain text content.");
        
        // Assert: Should succeed
        var doc = JsonDocument.Parse(artifactJson);
        Assert.IsFalse(doc.RootElement.TryGetProperty("error", out _), "Should NOT have error");
        Assert.IsTrue(doc.RootElement.TryGetProperty("path", out var path), "Should have path");
        
        // Verify file actually exists and has correct content
        var artifactFolder = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts");
        var filePath = Path.Combine(artifactFolder, "notes.txt");
        Assert.IsTrue(File.Exists(filePath), "File should exist");
        Assert.AreEqual("Plain text content.", File.ReadAllText(filePath));
    }

    [TestMethod]
    public void AddArtifact_BinaryExtension_Rejected()
    {
        // Arrange
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        // Act: Try to add a binary artifact via AddArtifact
        var artifactJson = _tools.AddArtifact(taskId, "diagram.png", "not actually an image");
        
        // Assert: Should reject with error
        var doc = JsonDocument.Parse(artifactJson);
        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out var error), "Should have error");
        
        // File should NOT exist
        var artifactFolder = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts");
        var filePath = Path.Combine(artifactFolder, "diagram.png");
        Assert.IsFalse(File.Exists(filePath), "File should NOT exist");
    }

    [TestMethod]
    public void AddArtifact_AtomicWrite_NoTempFile()
    {
        // Arrange
        var taskJson = _tools.AddTask("Test Task");
        var taskId = JsonDocument.Parse(taskJson).RootElement.GetProperty("task_id").GetString()!;
        
        // Act: Add artifact
        var artifactJson = _tools.AddArtifact(taskId, "plan.md", "# Plan content");
        
        // Assert: No .tmp file should remain
        var artifactFolder = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.artifacts");
        var tmpFiles = Directory.GetFiles(artifactFolder, "*.tmp");
        Assert.AreEqual(0, tmpFiles.Length, "Should NOT have any .tmp files");
        
        // Target file should exist
        var filePath = Path.Combine(artifactFolder, "plan.md");
        Assert.IsTrue(File.Exists(filePath), "Target file should exist");
        Assert.AreEqual("# Plan content", File.ReadAllText(filePath));
    }
}
