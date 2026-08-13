using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;
using Glasswork.TestInfrastructure;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class QueryTasksToolTests
{
    private string _vaultDir = null!;
    private GlassworkTools _tools = null!;
    private VaultService _vault = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultDir = Path.Combine(Path.GetTempPath(), "glasswork-mcp-query-tests", Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public void QueryTasks_FiltersTypedPredicatesAndReturnsDependencyReadBasis()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "dependency",
            Title = "Dependency",
            Status = GlassworkTask.Statuses.Done,
            Tags = ["foundation"],
        });
        _vault.Save(new GlassworkTask
        {
            Id = "parent",
            Title = "Parent",
            Type = GlassworkTask.Types.Pbi,
        });
        _vault.Save(new GlassworkTask
        {
            Id = "ready",
            Title = "Ready task",
            Status = GlassworkTask.Statuses.Todo,
            Type = GlassworkTask.Types.Task,
            Parent = "parent",
            Tags = ["workflow", "ready"],
            BlockedBy = ["dependency"],
        });
        _vault.Save(new GlassworkTask
        {
            Id = "unrelated",
            Title = "Unrelated",
            Status = GlassworkTask.Statuses.Todo,
            Tags = ["workflow"],
        });

        using var document = JsonDocument.Parse(_tools.QueryTasks(
            status: ["todo"],
            type: "task",
            tags: ["ready"],
            blocked_by_status: ["done"],
            parent_task_id: "parent",
            limit: 10));
        var root = document.RootElement;

        var tasks = root.GetProperty("tasks");
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("ready", tasks[0].GetProperty("id").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(tasks[0].GetProperty("resource_revision").GetString()));
        CollectionAssert.AreEqual(
            new[] { "next_cursor", "read_basis", "tasks" },
            root.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "blocked_by", "description", "id", "notes", "parent_id",
                "resource_revision", "status", "tags", "title", "type",
            },
            tasks[0].EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

        var readBasis = root.GetProperty("read_basis");
        CollectionAssert.AreEquivalent(
            new[] { "dependency" },
            readBasis.EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray());
        Assert.IsTrue(readBasis.EnumerateArray().All(item =>
            !string.IsNullOrWhiteSpace(item.GetProperty("resource_revision").GetString())));
    }

    [TestMethod]
    public void QueryTasks_BlockedByEmptySelectsOnlyTasksWithoutDependencies()
    {
        _vault.Save(new GlassworkTask { Id = "free", Title = "Free", Created = new DateTime(2026, 1, 2) });
        _vault.Save(new GlassworkTask
        {
            Id = "dependent",
            Title = "Dependent",
            Created = new DateTime(2026, 1, 1),
            BlockedBy = ["free"],
        });

        using var document = JsonDocument.Parse(_tools.QueryTasks(blocked_by_empty: true));

        CollectionAssert.AreEqual(
            new[] { "free" },
            document.RootElement.GetProperty("tasks").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()).ToArray());
    }

    [TestMethod]
    public void QueryTasks_DoesNotScanUnrelatedVaultMarkdown()
    {
        _vault.Save(new GlassworkTask { Id = "task", Title = "Task" });
        using var unreadable = UnreadableDirectoryScope.Create(
            Path.Combine(_vaultDir, "unrelated-private"));

        using var document = JsonDocument.Parse(_tools.QueryTasks());

        Assert.IsFalse(document.RootElement.TryGetProperty("error", out _));
        Assert.AreEqual(
            "task",
            document.RootElement.GetProperty("tasks")[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void QueryTasks_PreservesLegacyStatusAndIgnoresMalformedBlockedMetadata()
    {
        File.WriteAllText(Path.Combine(_vault.VaultPath, "legacy.md"), """
            ---
            id: legacy
            title: Legacy
            status: someday
            ---
            """);
        File.WriteAllText(Path.Combine(_vault.VaultPath, "malformed-blocked.md"), """
            ---
            id: malformed-blocked
            title: Malformed blocked
            status: blocked
            blocked_reason: Waiting
            blocked_at: 2026-08-12T12:00:00Z
            blocked_from_status: doing
            ---
            """);

        using var document = JsonDocument.Parse(_tools.QueryTasks());
        var statuses = document.RootElement.GetProperty("tasks")
            .EnumerateArray()
            .ToDictionary(
                task => task.GetProperty("id").GetString()!,
                task => task.GetProperty("status").GetString());

        Assert.AreEqual("someday", statuses["legacy"]);
        Assert.AreEqual("blocked", statuses["malformed-blocked"]);
    }

    [TestMethod]
    public void QueryTasks_InvalidRelationshipsReturnStructuredDiagnostics()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "self",
            Title = "Self",
            BlockedBy = ["self", "missing"],
        });

        using var document = JsonDocument.Parse(_tools.QueryTasks());
        var root = document.RootElement;

        Assert.AreEqual("validation_error", root.GetProperty("error").GetString());
        CollectionAssert.AreEquivalent(
            new[] { "self_dependency", "missing_dependency" },
            root.GetProperty("diagnostics").EnumerateArray()
                .Select(item => item.GetProperty("code").GetString()).ToArray());
    }

    [TestMethod]
    public void QueryTasks_UsesDeterministicBoundedContinuation()
    {
        foreach (var id in new[] { "c", "a", "b" })
        {
            _vault.Save(new GlassworkTask
            {
                Id = id,
                Title = id,
                Created = new DateTime(2026, 1, 1),
            });
        }

        using var first = JsonDocument.Parse(_tools.QueryTasks(order_by: "id", limit: 2));
        var firstRoot = first.RootElement;
        CollectionAssert.AreEqual(
            new[] { "a", "b" },
            firstRoot.GetProperty("tasks").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()).ToArray());

        var cursor = firstRoot.GetProperty("next_cursor").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(cursor));

        using var second = JsonDocument.Parse(_tools.QueryTasks(order_by: "id", limit: 2, cursor: cursor));
        CollectionAssert.AreEqual(
            new[] { "c" },
            second.RootElement.GetProperty("tasks").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()).ToArray());
        Assert.AreEqual(JsonValueKind.Null, second.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [TestMethod]
    public void QueryTasks_CursorAcceptsEquivalentNormalizedTagFilters()
    {
        foreach (var id in new[] { "a", "b" })
        {
            _vault.Save(new GlassworkTask
            {
                Id = id,
                Title = id,
                Tags = ["Ready"],
            });
        }

        using var first = JsonDocument.Parse(_tools.QueryTasks(tags: ["Ready"], limit: 1));
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(cursor));

        using var second = JsonDocument.Parse(_tools.QueryTasks(
            tags: ["ready", "READY"],
            limit: 1,
            cursor: cursor));

        Assert.IsFalse(second.RootElement.TryGetProperty("error", out _));
        Assert.AreEqual(
            "b",
            second.RootElement.GetProperty("tasks")[0].GetProperty("id").GetString());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(101)]
    public void QueryTasks_InvalidLimitPreservesErrorContract(int limit)
    {
        using var document = JsonDocument.Parse(_tools.QueryTasks(limit: limit));

        AssertError(
            document.RootElement,
            "invalid_limit",
            "limit must be between 1 and 100.");
    }

    [TestMethod]
    public void QueryTasks_InvalidLimitRetainsFirstValidationPrecedence()
    {
        using var document = JsonDocument.Parse(_tools.QueryTasks(
            order_by: "created",
            limit: 0));

        AssertError(
            document.RootElement,
            "invalid_limit",
            "limit must be between 1 and 100.");
    }

    [TestMethod]
    public void QueryTasks_InvalidOrderPreservesErrorContract()
    {
        using var document = JsonDocument.Parse(_tools.QueryTasks(order_by: "created"));

        AssertError(
            document.RootElement,
            "invalid_order",
            "order_by must be 'created_id' or 'id'.");
    }

    [TestMethod]
    public void QueryTasks_InvalidTypePreservesErrorContract()
    {
        using var document = JsonDocument.Parse(_tools.QueryTasks(type: "feature"));

        AssertError(
            document.RootElement,
            "invalid_type",
            "type must be 'task', 'pbi', or 'bug'.");
    }

    [TestMethod]
    public void QueryTasks_ConflictingRelationshipPredicatesPreserveErrorContract()
    {
        using var document = JsonDocument.Parse(_tools.QueryTasks(
            blocked_by_empty: true,
            blocked_by_status: ["done"]));

        AssertError(
            document.RootElement,
            "invalid_relationship_predicate",
            "blocked_by_empty cannot be combined with blocked_by_status.");
    }

    [TestMethod]
    public void QueryTasks_InvalidCursorPreservesErrorContract()
    {
        using var document = JsonDocument.Parse(_tools.QueryTasks(cursor: "not-a-cursor"));

        AssertError(
            document.RootElement,
            "invalid_cursor",
            "The continuation cursor is invalid.");
    }

    private static void AssertError(
        JsonElement root,
        string expectedCode,
        string expectedMessage)
    {
        CollectionAssert.AreEqual(
            new[] { "error", "message" },
            root.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.AreEqual(expectedCode, root.GetProperty("error").GetString());
        Assert.AreEqual(expectedMessage, root.GetProperty("message").GetString());
    }
}
