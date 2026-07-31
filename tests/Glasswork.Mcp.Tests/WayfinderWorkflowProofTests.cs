using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class WayfinderWorkflowProofTests
{
    private string _vaultDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(
            Path.GetTempPath(),
            "glasswork-mcp-wayfinder-proof",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_vaultDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultDir))
            Directory.Delete(_vaultDir, recursive: true);
    }

    [TestMethod]
    public async Task GenericMcpContractsProveCompleteWayfinderShapedWorkflow()
    {
        var tools = NewTools();
        using var map = JsonDocument.Parse("""
        [
          { "op": "create_task", "task_id": "parent-pbi", "if_absent": true,
            "fields": {
              "title": "Wayfinder proof map",
              "type": "pbi",
              "tags": ["wayfinder-map", "reserved:pbi"],
              "description": "## Goal\nProve generic MCP composition.",
              "notes": "Map index: children are resolved atomically."
            } },
          { "op": "create_task", "task_id": "dependency-done", "if_absent": true,
            "fields": { "title": "Completed dependency", "status": "done",
              "tags": ["wayfinder-dependency"] } },
          { "op": "create_task", "task_id": "child-a", "if_absent": true,
            "fields": {
              "title": "First child",
              "parent_task_id": "parent-pbi",
              "tags": ["wayfinder-child", "reserved:task"],
              "description": "## Work\nFirst child framing.",
              "notes": "Not started."
            } },
          { "op": "create_task", "task_id": "child-b", "if_absent": true,
            "fields": {
              "title": "Second child",
              "parent_task_id": "parent-pbi",
              "tags": ["wayfinder-child", "reserved:task"],
              "description": "## Work\nSecond child framing.",
              "notes": "Not started."
            } },
          { "op": "replace_task_relationships", "task_id": "child-a",
            "relationship": "blocked_by", "targets": ["dependency-done"] },
          { "op": "replace_task_relationships", "task_id": "child-b",
            "relationship": "blocked_by", "targets": ["dependency-done"] }
        ]
        """);

        var mapResult = JsonDocument.Parse(tools.TransactTasks("map-1", map.RootElement)).RootElement;
        Assert.AreEqual("applied", mapResult.GetProperty("outcome").GetString());
        Assert.AreEqual(4, mapResult.GetProperty("tasks").GetArrayLength());
        Assert.AreEqual(GlassworkTask.Types.Pbi, Load("parent-pbi").Type);
        CollectionAssert.AreEquivalent(
            new[] { "wayfinder-map", "reserved:pbi" },
            Load("parent-pbi").Tags);
        StringAssert.Contains(Load("parent-pbi").Description, "## Goal");
        Assert.AreEqual("Map index: children are resolved atomically.", Load("parent-pbi").Notes);
        CollectionAssert.AreEqual(new[] { "dependency-done" }, Load("child-a").BlockedBy);

        using var frontier = JsonDocument.Parse(tools.QueryTasks(
            parent_task_id: "parent-pbi",
            status: [GlassworkTask.Statuses.Todo],
            type: GlassworkTask.Types.Task,
            tags: ["wayfinder-child"],
            blocked_by_status: [GlassworkTask.Statuses.Done],
            order_by: "id",
            limit: 10));
        var frontierRoot = frontier.RootElement;
        CollectionAssert.AreEqual(
            new[] { "child-a", "child-b" },
            frontierRoot.GetProperty("tasks").EnumerateArray()
                .Select(task => task.GetProperty("id").GetString()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "dependency-done" },
            frontierRoot.GetProperty("read_basis").EnumerateArray()
                .Select(task => task.GetProperty("id").GetString()).ToArray());

        var dependencyRevision = frontierRoot.GetProperty("read_basis")[0]
            .GetProperty("resource_revision").GetString()!;
        var childBRevision = frontierRoot.GetProperty("tasks")[1]
            .GetProperty("resource_revision").GetString()!;
        var claimOperations = BuildClaim(
            "child-b",
            childBRevision,
            "in-progress",
            dependencyRevision);
        var contenderOne = NewTools();
        var contenderTwo = NewTools();

        var claims = await Task.WhenAll(
            Task.Run(() => contenderOne.TransactTasks("claim-one", claimOperations.RootElement)),
            Task.Run(() => contenderTwo.TransactTasks("claim-two", claimOperations.RootElement)));
        var claimResults = claims.Select(claim => JsonDocument.Parse(claim)).ToArray();
        Assert.AreEqual(1, claimResults.Count(result =>
            result.RootElement.GetProperty("outcome").GetString() == "applied"));
        Assert.AreEqual(1, claimResults.Count(result =>
            result.RootElement.GetProperty("error").GetString() == "conflict"));
        Assert.AreEqual("in-progress", Load("child-b").Status);
        Assert.AreEqual("todo", Load("child-a").Status);

        var childARevisionBeforeDependencyChange = Revision("child-a");
        using var changeDependency = JsonDocument.Parse($$"""
        [
          { "op": "set_task_fields", "task_id": "dependency-done",
            "if_revision": "{{dependencyRevision}}",
            "fields": { "status": "todo" } }
        ]
        """);
        using var dependencyChange = JsonDocument.Parse(tools.TransactTasks(
            "dependency-change", changeDependency.RootElement));
        Assert.AreEqual("applied", dependencyChange.RootElement.GetProperty("outcome").GetString());

        using var staleClaim = JsonDocument.Parse($$"""
        [
          { "op": "assert_task_revision", "task_id": "dependency-done",
            "if_revision": "{{dependencyRevision}}" },
          { "op": "set_task_fields", "task_id": "child-a",
            "if_revision": "{{childARevisionBeforeDependencyChange}}",
            "fields": { "status": "in-progress" } }
        ]
        """);
        var staleResult = JsonDocument.Parse(tools.TransactTasks("stale-claim", staleClaim.RootElement)).RootElement;
        Assert.AreEqual("conflict", staleResult.GetProperty("error").GetString());
        CollectionAssert.Contains(
            staleResult.GetProperty("diagnostics").EnumerateArray()
                .SelectMany(diagnostic => diagnostic.GetProperty("task_ids").EnumerateArray())
                .Select(taskId => taskId.GetString()).ToArray(),
            "dependency-done");

        using var refreshed = JsonDocument.Parse(tools.QueryTasks(
            parent_task_id: "parent-pbi",
            status: [GlassworkTask.Statuses.Todo],
            type: GlassworkTask.Types.Task,
            tags: ["wayfinder-child"],
            blocked_by_status: [GlassworkTask.Statuses.Done],
            order_by: "id",
            limit: 10));
        Assert.AreEqual(0, refreshed.RootElement.GetProperty("tasks").GetArrayLength());

        var parentRevision = Revision("parent-pbi");
        var claimedChildRevision = Revision("child-b");
        using var resolution = JsonDocument.Parse($$"""
        [
          { "op": "set_task_fields", "task_id": "child-b",
            "if_revision": "{{claimedChildRevision}}",
            "fields": {
              "status": "done",
              "notes": "Resolution: completed by the generic workflow."
            } },
          { "op": "set_task_fields", "task_id": "parent-pbi",
            "if_revision": "{{parentRevision}}",
            "fields": { "notes": { "append": "[[child-b]]: completed" } } }
        ]
        """);
        var failingResolution = new GlassworkTools(
            new VaultContext(_vaultDir),
            faults: new ThrowOnOccurrenceFault(ResourceMutationFailurePoint.DuringReplacement, 2));
        var failedResolution = JsonDocument.Parse(
            failingResolution.TransactTasks("resolve-child-b-failure", resolution.RootElement)).RootElement;
        Assert.AreEqual("operation_failed", failedResolution.GetProperty("error").GetString());
        Assert.AreEqual("in-progress", Load("child-b").Status);
        Assert.AreEqual("Not started.", Load("child-b").Notes.Split('\n').Last());
        Assert.IsFalse(Load("parent-pbi").Notes.Contains("[[child-b]]: completed", StringComparison.Ordinal));

        var responseLoss = new GlassworkTools(
            new VaultContext(_vaultDir),
            faults: new ThrowOnceFault(ResourceMutationFailurePoint.AfterCommit));
        var lostResponse = JsonDocument.Parse(
            responseLoss.TransactTasks("resolve-child-b", resolution.RootElement)).RootElement;
        Assert.AreEqual("operation_failed", lostResponse.GetProperty("error").GetString());

        var reconstructed = NewTools();
        var replay = JsonDocument.Parse(
            reconstructed.TransactTasks("resolve-child-b", resolution.RootElement)).RootElement;
        Assert.IsTrue(replay.GetProperty("replayed").GetBoolean());
        Assert.AreEqual("done", Load("child-b").Status);
        Assert.AreEqual("Resolution: completed by the generic workflow.", Load("child-b").Notes);
        Assert.AreEqual(1, Load("parent-pbi").Notes.Split("[[child-b]]: completed").Length - 1);
        CollectionAssert.AreEquivalent(
            new[] { "parent-pbi", "dependency-done", "child-a", "child-b" },
            Directory.GetFiles(Path.Combine(_vaultDir, "wiki", "todo"), "*.md")
                .Select(Path.GetFileNameWithoutExtension).ToArray());

        var finalTools = NewTools();
        using var finalRead = JsonDocument.Parse(finalTools.QueryTasks(
            status: [GlassworkTask.Statuses.Done],
            order_by: "id",
            limit: 10));
        CollectionAssert.Contains(
            finalRead.RootElement.GetProperty("tasks").EnumerateArray()
                .Select(task => task.GetProperty("id").GetString()).ToArray(),
            "child-b");
    }

    private GlassworkTools NewTools() => new(new VaultContext(_vaultDir));

    private GlassworkTask Load(string taskId) =>
        new VaultService(Path.Combine(_vaultDir, "wiki", "todo")).Load(taskId)!;

    private string Revision(string taskId)
    {
        using var result = JsonDocument.Parse(NewTools().QueryTasks(order_by: "id", limit: 100));
        return result.RootElement.GetProperty("tasks").EnumerateArray()
            .Single(task => task.GetProperty("id").GetString() == taskId)
            .GetProperty("resource_revision").GetString()!;
    }

    private JsonDocument BuildClaim(
        string taskId,
        string revision,
        string status,
        string dependencyRevision) =>
        JsonDocument.Parse($$"""
        [
          { "op": "assert_task_revision", "task_id": "dependency-done",
            "if_revision": "{{dependencyRevision}}" },
          { "op": "set_task_fields", "task_id": "{{taskId}}",
            "if_revision": "{{revision}}", "fields": { "status": "{{status}}" } }
        ]
        """);

    private sealed class ThrowOnceFault(ResourceMutationFailurePoint point) : IResourceMutationFaultInjector
    {
        private bool _thrown;

        public void ThrowIfInjected(ResourceMutationFailurePoint candidate)
        {
            if (candidate == point && !_thrown)
            {
                _thrown = true;
                throw new IOException("Injected proof failure.");
            }
        }
    }

    private sealed class ThrowOnOccurrenceFault(
        ResourceMutationFailurePoint point,
        int occurrence) : IResourceMutationFaultInjector
    {
        private int _count;

        public void ThrowIfInjected(ResourceMutationFailurePoint candidate)
        {
            if (candidate == point && ++_count == occurrence)
                throw new IOException("Injected proof failure.");
        }
    }
}
