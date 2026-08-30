using System.Text.Json;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class ChildActivitySummaryToolTests
{
    private string _vaultRoot = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Initialize()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-child-summary-tool-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultRoot);
        _tools = new GlassworkTools(
            new VaultContext(_vaultRoot),
            clock: () => new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void CaptureAndRefresh_UseBoundedContextAndCreateStableArtifact()
    {
        var parentId = JsonDocument.Parse(_tools.AddTask("Parent", type: "parent"))
            .RootElement.GetProperty("task_id").GetString()!;
        _tools.AddTask(
            "Child",
            description: "Description is outside summary input.",
            notes: "Durable note",
            parent_task_id: parentId);

        using var context = JsonDocument.Parse(
            _tools.GetChildActivitySummaryContext(parentId));
        var root = context.RootElement;
        var task = root.GetProperty("groups")[0].GetProperty("tasks")[0];
        Assert.AreEqual("Durable note", task.GetProperty("notes").GetString());
        Assert.IsFalse(task.TryGetProperty("description", out _));
        StringAssert.Contains(root.GetProperty("instruction").GetString(), "Do not use chat transcripts");
        var basis = root.GetProperty("read_basis")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);

        using var result = JsonDocument.Parse(_tools.RefreshChildActivitySummary(
            parentId,
            "## Child\n\nCurrent activity.",
            root.GetProperty("parent_revision").GetString()!,
            root.GetProperty("descendant_count").GetInt32(),
            basis,
            "summary-tool-create",
            root.GetProperty("expected_summary_revision").GetString()));

        Assert.AreEqual("applied", result.RootElement.GetProperty("outcome").GetString());
        var path = Path.Combine(
            _vaultRoot,
            "wiki",
            "todo",
            parentId + ".artifacts",
            "child-activity-summary.md");
        Assert.IsTrue(File.Exists(path));
        StringAssert.Contains(File.ReadAllText(path), "Current activity.");
    }

    [TestMethod]
    public void AddArtifact_RejectsManagedSummaryFilename()
    {
        var parentId = JsonDocument.Parse(_tools.AddTask("Parent", type: "parent"))
            .RootElement.GetProperty("task_id").GetString()!;

        using var result = JsonDocument.Parse(_tools.AddArtifact(
            parentId,
            "child-activity-summary.md",
            "bypass"));

        Assert.AreEqual(
            "reserved_artifact",
            result.RootElement.GetProperty("error").GetString());
    }
}
