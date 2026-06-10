using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class RemoveLinkToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-remove-link-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
        _vault = new VaultService(Path.Combine(_vaultDir, "wiki", "todo"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    // ───────────────────────────── remove_link ────────────────────────────

    [TestMethod]
    public void RemoveLink_RemovesSingleLink()
    {
        // Arrange: create task with one link
        var addJson = _tools.AddTask("Test task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddLink(taskId, "pr", "https://github.com/owner/repo/pull/123", "Fix bug");

        // Act: remove the link
        var removeJson = _tools.RemoveLink(taskId, "https://github.com/owner/repo/pull/123");

        // Assert: response shape
        var doc = JsonDocument.Parse(removeJson);
        Assert.AreEqual(taskId, doc.RootElement.GetProperty("task_id").GetString());
        
        var removedLink = doc.RootElement.GetProperty("link");
        Assert.AreEqual("pr", removedLink.GetProperty("type").GetString());
        Assert.AreEqual("https://github.com/owner/repo/pull/123", removedLink.GetProperty("url").GetString());
        Assert.AreEqual("Fix bug", removedLink.GetProperty("title").GetString());
        
        Assert.AreEqual(0, doc.RootElement.GetProperty("total_links").GetInt32());

        // Assert: vault file has no links
        var task = _vault.Load(taskId);
        Assert.AreEqual(0, task!.Links.Count, "Task must have no links after removal.");
    }

    [TestMethod]
    public void RemoveLink_PreservesOtherLinks()
    {
        // Arrange: create task with multiple links
        var addJson = _tools.AddTask("Multi-link task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddLink(taskId, "pr", "https://pr1", "PR 1");
        _tools.AddLink(taskId, "ado", "123456", "Work Item");
        _tools.AddLink(taskId, "doc", "https://doc1", "Design Doc");

        // Act: remove middle link
        var removeJson = _tools.RemoveLink(taskId, "123456");

        // Assert: response
        var doc = JsonDocument.Parse(removeJson);
        Assert.AreEqual(2, doc.RootElement.GetProperty("total_links").GetInt32());

        // Assert: vault file preserves order
        var task = _vault.Load(taskId);
        Assert.AreEqual(2, task!.Links.Count);
        Assert.AreEqual("pr", task.Links[0].Type);
        Assert.AreEqual("https://pr1", task.Links[0].Value);
        Assert.AreEqual("doc", task.Links[1].Type);
        Assert.AreEqual("https://doc1", task.Links[1].Value);
    }

    [TestMethod]
    public void RemoveLink_UrlNotFound_ReturnsError()
    {
        // Arrange: task with one link
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddLink(taskId, "pr", "https://existing", null);

        // Act: try to remove non-existent URL
        var json = _tools.RemoveLink(taskId, "https://nonexistent");
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert
        Assert.IsTrue(result.TryGetProperty("error", out _));
        Assert.AreEqual("link_not_found", result.GetProperty("error").GetString());
    }

    [TestMethod]
    public void RemoveLink_TaskNotFound_ReturnsError()
    {
        // Act
        var json = _tools.RemoveLink("nonexistent", "https://any");
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert
        Assert.IsTrue(result.TryGetProperty("error", out _));
        Assert.AreEqual("not_found", result.GetProperty("error").GetString());
    }

    [TestMethod]
    public void RemoveLink_TypeDisambiguates()
    {
        // Arrange: same URL under two types
        var addJson = _tools.AddTask("Ambiguous task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddLink(taskId, "pr", "https://github.com/owner/repo/pull/1", "PR");
        _tools.AddLink(taskId, "doc", "https://github.com/owner/repo/pull/1", "Doc");

        // Act: remove pr type only
        var removeJson = _tools.RemoveLink(taskId, "https://github.com/owner/repo/pull/1", "pr");

        // Assert: only pr removed, doc remains
        var doc = JsonDocument.Parse(removeJson);
        Assert.AreEqual("pr", doc.RootElement.GetProperty("link").GetProperty("type").GetString());
        Assert.AreEqual(1, doc.RootElement.GetProperty("total_links").GetInt32());

        var task = _vault.Load(taskId);
        Assert.AreEqual(1, task!.Links.Count);
        Assert.AreEqual("doc", task.Links[0].Type);
    }

    [TestMethod]
    public void RemoveLink_TypeMismatch_ReturnsError()
    {
        // Arrange: task with pr link
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddLink(taskId, "pr", "https://github.com/pull/1", "PR");

        // Act: try to remove with wrong type
        var json = _tools.RemoveLink(taskId, "https://github.com/pull/1", "ado");
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert
        Assert.IsTrue(result.TryGetProperty("error", out _));
        Assert.AreEqual("link_not_found", result.GetProperty("error").GetString());
    }

    [TestMethod]
    public void RemoveLink_AmbiguousWithoutType_ReturnsError()
    {
        // Arrange: same URL under two types
        var addJson = _tools.AddTask("Ambiguous task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddLink(taskId, "pr", "https://url", "PR");
        _tools.AddLink(taskId, "doc", "https://url", "Doc");

        // Act: try to remove without type
        var json = _tools.RemoveLink(taskId, "https://url");
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert: ambiguous error
        Assert.IsTrue(result.TryGetProperty("error", out _));
        Assert.AreEqual("ambiguous_link", result.GetProperty("error").GetString());
    }

    [TestMethod]
    public void RemoveLink_TrimsParity()
    {
        // Arrange: add link with trimmed URL (add_link trims)
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;
        _tools.AddLink(taskId, "pr", " https://github.com/pull/1 ", "PR");

        // Act: remove with different whitespace
        var removeJson = _tools.RemoveLink(taskId, "https://github.com/pull/1");

        // Assert: removal succeeded
        var doc = JsonDocument.Parse(removeJson);
        Assert.AreEqual(0, doc.RootElement.GetProperty("total_links").GetInt32());
    }

    [TestMethod]
    public void RemoveLink_InvalidType_ReturnsError()
    {
        // Arrange
        var addJson = _tools.AddTask("Task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: invalid type
        var json = _tools.RemoveLink(taskId, "https://url", "gibberish");
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert
        Assert.IsTrue(result.TryGetProperty("error", out _));
        Assert.AreEqual("invalid_link_type", result.GetProperty("error").GetString());
    }
}
