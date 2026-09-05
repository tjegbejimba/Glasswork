using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public class AddLinkToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    private string TasksDir => Path.Combine(_vaultDir, "wiki", "todo");

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-add-link-tests", Guid.NewGuid().ToString("N"));
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

    // ───────────────────────────── add_link ────────────────────────────

    [TestMethod]
    public void AddLink_AddsLinkToTask()
    {
        // Arrange: create a task
        var addJson = _tools.AddTask("Test task");
        var taskId = JsonDocument.Parse(addJson).RootElement.GetProperty("task_id").GetString()!;

        // Act: add a PR link
        var linkJson = _tools.AddLink(taskId, link_type: "pr", url: "https://github.com/owner/repo/pull/123", title: "Fix bug");

        // Assert: response shape
        var doc = JsonDocument.Parse(linkJson);
        Assert.AreEqual(taskId, doc.RootElement.GetProperty("task_id").GetString());

        var link = doc.RootElement.GetProperty("link");
        Assert.AreEqual("pr", link.GetProperty("type").GetString());
        Assert.AreEqual("https://github.com/owner/repo/pull/123", link.GetProperty("url").GetString());
        Assert.AreEqual("Fix bug", link.GetProperty("title").GetString());

        Assert.AreEqual(1, doc.RootElement.GetProperty("total_links").GetInt32());

        // Assert: vault file has link in frontmatter
        var task = _vault.Load(taskId);
        Assert.HasCount(1, task.Links, "Task must have exactly one link.");
        Assert.AreEqual("pr", task.Links[0].Type);
        Assert.AreEqual("https://github.com/owner/repo/pull/123", task.Links[0].Value);
        Assert.AreEqual("Fix bug", task.Links[0].Label);
    }

    [TestMethod]
    public void AddLink_TaskNotFound_ReturnsError()
    {
        // Act
        var json = _tools.AddLink("nonexistent", "pr", "https://github.com/owner/repo/pull/1", null);
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert
        Assert.IsTrue(result.TryGetProperty("error", out _));
        Assert.AreEqual("not_found", result.GetProperty("error").GetString());
    }

    [TestMethod]
    public void AddLink_MultipleLinks_PreservesOrder()
    {
        // Arrange
        var taskId = "multi-link";
        var task = new GlassworkTask { Id = taskId, Title = "Multi-link task" };
        _vault.Save(task);

        // Act - add multiple links
        _tools.AddLink(taskId, "pr", "https://pr1", "PR 1");
        _tools.AddLink(taskId, "ado", "123456", "Work Item");
        _tools.AddLink(taskId, "doc", "https://doc1", "Design Doc");

        // Assert - verify order preserved
        var reloaded = _vault.Load(taskId);
        Assert.IsNotNull(reloaded);
        Assert.HasCount(3, reloaded.Links);
        Assert.AreEqual("pr", reloaded.Links[0].Type);
        Assert.AreEqual("https://pr1", reloaded.Links[0].Value);
        Assert.AreEqual("ado", reloaded.Links[1].Type);
        Assert.AreEqual("123456", reloaded.Links[1].Value);
        Assert.AreEqual("doc", reloaded.Links[2].Type);
        Assert.AreEqual("https://doc1", reloaded.Links[2].Value);
    }

    [TestMethod]
    public void AddLink_WithoutTitle_SucceedsWithNullLabel()
    {
        // Arrange
        var taskId = "no-title";
        var task = new GlassworkTask { Id = taskId, Title = "Task without title" };
        _vault.Save(task);

        // Act
        var json = _tools.AddLink(taskId, "incident", "INC-456789", null);
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert
        Assert.AreEqual(taskId, result.GetProperty("task_id").GetString());
        var linkElement = result.GetProperty("link");
        Assert.IsFalse(linkElement.TryGetProperty("title", out var titleProp) &&
                      titleProp.ValueKind != JsonValueKind.Null);

        var reloaded = _vault.Load(taskId);
        Assert.IsNull(reloaded!.Links[0].Label);
    }

    [TestMethod]
    public void AddLink_UnknownType_NormalizesToOther()
    {
        // Arrange
        var taskId = "unknown-type";
        var task = new GlassworkTask { Id = taskId, Title = "Unknown type task" };
        _vault.Save(task);

        // Act
        var json = _tools.AddLink(taskId, "unknown-type", "https://example.com", "Some Link");
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        // Assert
        Assert.AreEqual("other", result.GetProperty("link").GetProperty("type").GetString());

        var reloaded = _vault.Load(taskId);
        Assert.AreEqual("other", reloaded!.Links[0].Type);
    }
}
