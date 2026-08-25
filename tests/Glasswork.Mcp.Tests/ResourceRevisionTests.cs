using System.Text.Json;
using Glasswork.Core.Services;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class ResourceRevisionTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-revision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
        _tools = new GlassworkTools(new VaultContext(_vaultDir));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    [TestMethod]
    public void GetTask_RevisionDependsOnTaskBytesNotFilesystemMetadata()
    {
        var addResult = JsonDocument.Parse(_tools.AddTask("Revision task"));
        var taskId = addResult.RootElement.GetProperty("task_id").GetString()!;
        var taskPath = Path.Combine(_vaultDir, "wiki", "todo", $"{taskId}.md");

        var firstRevision = GetRevision(_tools.GetTask(taskId));
        File.SetLastWriteTimeUtc(taskPath, DateTime.UtcNow.AddMinutes(5));
        var sameBytesRevision = GetRevision(_tools.GetTask(taskId));

        File.AppendAllText(taskPath, "\nAdditional task bytes.\n");
        var changedBytesRevision = GetRevision(_tools.GetTask(taskId));

        Assert.AreEqual(firstRevision, sameBytesRevision);
        Assert.AreNotEqual(firstRevision, changedBytesRevision);
        StringAssert.StartsWith(firstRevision, "rr");
    }

    [TestMethod]
    public void GetCapabilities_AdvertisesTheCompleteEnforcedContract()
    {
        var result = JsonDocument.Parse(new CapabilityTools().GetCapabilities());
        var root = result.RootElement;

        Assert.AreEqual("1.0", root.GetProperty("contract_version").GetString());
        CollectionAssert.Contains(
            root.GetProperty("implemented_capabilities").EnumerateArray().Select(x => x.GetString()).ToArray(),
            "resource_revisions");
        var implemented = root.GetProperty("implemented_capabilities")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
        var future = root.TryGetProperty("future_capabilities", out var futureProperty)
            ? futureProperty.EnumerateArray().Select(x => x.GetString()).ToArray()
            : [];

        CollectionAssert.Contains(implemented, "relation_aware_queries");
        CollectionAssert.Contains(implemented, "read_assertions");
        CollectionAssert.Contains(implemented, "typed_transactions");
        CollectionAssert.Contains(implemented, "complete_set_relationships");
        CollectionAssert.Contains(implemented, "transaction_idempotency");
        CollectionAssert.Contains(implemented, "recoverable_all_or_none_commit");
        CollectionAssert.Contains(implemented, "guarded_hard_deletion");
        CollectionAssert.Contains(implemented, "authoritative_ado_reconciliation");
        Assert.IsEmpty(future);
    }

    [TestMethod]
    public void TaskBearingReadResults_IncludeResourceRevisions()
    {
        var parent = JsonDocument.Parse(_tools.AddTask("Read contract parent", my_day: true, type: "parent"));
        var parentId = parent.RootElement.GetProperty("task_id").GetString()!;
        var child = JsonDocument.Parse(_tools.AddTask(
            "Read contract child",
            parent_task_id: parentId,
            description: "Searchable contract text"));
        var childId = child.RootElement.GetProperty("task_id").GetString()!;
        _tools.AddTask("Read contract overdue", due_date: DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"));

        var list = JsonDocument.Parse(_tools.ListTasks()).RootElement.GetProperty("tasks");
        Assert.IsTrue(list.EnumerateArray().All(HasRevision));

        var projected = JsonDocument.Parse(_tools.ListTasks(fields: ["title"])).RootElement.GetProperty("tasks");
        Assert.IsTrue(projected.EnumerateArray().All(HasRevision));

        var search = JsonDocument.Parse(_tools.SearchTasks("contract")).RootElement.GetProperty("tasks");
        Assert.IsTrue(search.EnumerateArray().All(HasRevision));

        var myDay = JsonDocument.Parse(_tools.GetMyDay()).RootElement.GetProperty("tasks");
        Assert.IsTrue(myDay.EnumerateArray().All(HasRevision));

        var subtasks = JsonDocument.Parse(_tools.ListSubtasks(parentId)).RootElement;
        Assert.IsTrue(HasRevision(subtasks.GetProperty("parent")));
        Assert.IsTrue(subtasks.GetProperty("subtasks").EnumerateArray().All(HasRevision));

        var task = JsonDocument.Parse(_tools.GetTask(childId)).RootElement;
        Assert.IsTrue(HasRevision(task));

        var context = JsonDocument.Parse(_tools.LoadContext(parentId)).RootElement;
        Assert.IsTrue(HasRevision(context.GetProperty("task")));
        Assert.IsTrue(context.GetProperty("subtasks").EnumerateArray()
            .All(subtree => HasRevision(subtree.GetProperty("task"))));

        var handoff = JsonDocument.Parse(_tools.GetTaskContext(parentId)).RootElement;
        Assert.IsTrue(handoff.TryGetProperty("resource_revision", out var handoffRevision));
        Assert.IsFalse(string.IsNullOrWhiteSpace(handoffRevision.GetString()));

        var overdue = JsonDocument.Parse(_tools.ListOverdue()).RootElement.GetProperty("tasks");
        Assert.IsTrue(overdue.EnumerateArray().All(HasRevision));
    }

    private static bool HasRevision(JsonElement element) =>
        element.TryGetProperty("resource_revision", out var revision)
        && revision.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(revision.GetString());

    private static string GetRevision(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("resource_revision").GetString()!;
}
