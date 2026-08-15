using System.Text.Json;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.Mcp;
using Glasswork.Mcp.Tools;

namespace Glasswork.Mcp.Tests;

[TestClass]
public sealed class DeleteTaskToolTests
{
    private string _vaultRoot = null!;
    private string _todoPath = null!;
    private GlassworkTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-delete-task-tool-tests",
            Guid.NewGuid().ToString("N"));
        _todoPath = Path.Combine(_vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(_todoPath);
        _tools = new GlassworkTools(new VaultContext(_vaultRoot));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void DeleteTask_RequiresMutationRevisionAndConfirmationTitle()
    {
        using var created = JsonDocument.Parse(_tools.AddTask("Delete me"));
        var taskId = created.RootElement.GetProperty("task_id").GetString()!;
        var revision = created.RootElement.GetProperty("resource_revision").GetString();

        using var result = JsonDocument.Parse(_tools.DeleteTask(
            taskId,
            mutation_id: null,
            if_revision: revision,
            confirm_title: "Delete me",
            cascade_children: false));

        Assert.AreEqual("precondition_required", result.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, $"{taskId}.md")));
    }

    [TestMethod]
    public void DeleteTask_WithoutCascadeReturnsTheCompleteDescendantIds()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        vault.Save(new GlassworkTask { Id = "child-b", Title = "Child B", Parent = "root" });
        vault.Save(new GlassworkTask { Id = "child-a", Title = "Child A", Parent = "root" });
        vault.Save(new GlassworkTask { Id = "grandchild", Title = "Grandchild", Parent = "child-a" });
        using var current = JsonDocument.Parse(_tools.GetTask("root"));
        var revision = current.RootElement.GetProperty("resource_revision").GetString();

        using var result = JsonDocument.Parse(_tools.DeleteTask(
            "root",
            "delete-needs-cascade",
            revision,
            "Root",
            cascade_children: false));

        Assert.AreEqual(
            "descendants_require_cascade",
            result.RootElement.GetProperty("error").GetString());
        CollectionAssert.AreEqual(
            new[] { "child-a", "child-b", "grandchild" },
            result.RootElement.GetProperty("descendant_ids")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
        Assert.IsTrue(vault.Exists("root"));
        Assert.IsTrue(vault.Exists("grandchild"));
    }

    [TestMethod]
    public void PreflightDeleteTask_ReturnsTheOpaqueCascadeRevision()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        vault.Save(new GlassworkTask { Id = "child", Title = "Child", Parent = "root" });

        using var result = JsonDocument.Parse(_tools.PreflightDeleteTask("root"));

        Assert.AreEqual("ready", result.RootElement.GetProperty("outcome").GetString());
        StringAssert.StartsWith(
            result.RootElement.GetProperty("preflight_revision").GetString(),
            "dpr1-");
        CollectionAssert.AreEqual(
            new[] { "child" },
            result.RootElement.GetProperty("descendant_ids")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray());
    }

    [TestMethod]
    public void DeleteTask_CascadeRequiresTheReviewedPreflightRevision()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        vault.Save(new GlassworkTask { Id = "child", Title = "Child", Parent = "root" });
        var root = vault.Load("root")!;

        using var result = JsonDocument.Parse(_tools.DeleteTask(
            "root",
            "delete-without-preflight",
            root.ResourceRevision,
            root.Title,
            cascade_children: true));

        Assert.AreEqual(
            "precondition_required",
            result.RootElement.GetProperty("error").GetString());
        StringAssert.StartsWith(
            result.RootElement.GetProperty("preflight")
                .GetProperty("preflight_revision").GetString(),
            "dpr1-");
        Assert.IsTrue(vault.Exists("root"));
        Assert.IsTrue(vault.Exists("child"));
    }

    [TestMethod]
    public void DeleteTask_CascadesPbisAndReturnsTheCompleteMutationReport()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(new GlassworkTask
        {
            Id = "root-pbi",
            Title = "Root PBI",
            Type = GlassworkTask.Types.Pbi,
        });
        vault.Save(new GlassworkTask { Id = "child", Title = "Child", Parent = "root-pbi" });
        var artifacts = Path.Combine(_todoPath, "child.artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "plan.md"), "# Plan");
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Keep [[root-pbi|the PBI]] and [[child]].");
        using var current = JsonDocument.Parse(_tools.GetTask("root-pbi"));
        var revision = current.RootElement.GetProperty("resource_revision").GetString();
        using var preflight = JsonDocument.Parse(_tools.PreflightDeleteTask("root-pbi"));
        var preflightRevision = preflight.RootElement
            .GetProperty("preflight_revision")
            .GetString();

        using var first = JsonDocument.Parse(_tools.DeleteTask(
            "root-pbi",
            "delete-pbi-tree",
            revision,
            "Root PBI",
            cascade_children: true,
            if_preflight_revision: preflightRevision));
        using var replay = JsonDocument.Parse(_tools.DeleteTask(
            "root-pbi",
            "delete-pbi-tree",
            revision,
            "Root PBI",
            cascade_children: true,
            if_preflight_revision: preflightRevision));

        Assert.AreEqual("applied", first.RootElement.GetProperty("outcome").GetString());
        CollectionAssert.AreEqual(
            new[] { "root-pbi", "child" },
            first.RootElement.GetProperty("deleted_tasks")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "child" },
            first.RootElement.GetProperty("descendants")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString())
                .ToArray());
        Assert.AreEqual(
            "wiki/todo/child.artifacts/plan.md",
            first.RootElement.GetProperty("removed_artifacts")[0]
                .GetProperty("path").GetString());
        Assert.AreEqual(
            "wiki/concepts/links.md",
            first.RootElement.GetProperty("rewritten_backlink_pages")[0]
                .GetProperty("path").GetString());
        Assert.AreEqual("not_required", first.RootElement.GetProperty("recovery_outcome").GetString());
        Assert.IsTrue(replay.RootElement.GetProperty("replayed").GetBoolean());
        Assert.IsFalse(vault.Exists("root-pbi"));
        Assert.IsFalse(vault.Exists("child"));
        Assert.AreEqual("Keep the PBI and Child.", File.ReadAllText(pagePath));
    }

    [TestMethod]
    public void DeleteTask_OrdinaryFailureReturnsStructuredErrorAfterRollback()
    {
        var vault = new VaultService(_todoPath);
        vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Keep [[root]].");
        using var current = JsonDocument.Parse(_tools.GetTask("root"));
        var revision = current.RootElement.GetProperty("resource_revision").GetString();
        var failing = new GlassworkTools(
            new VaultContext(_vaultRoot),
            faults: new ThrowOnceFault(
                ResourceMutationFailurePoint.AfterReplacementBeforeCommit));

        using var result = JsonDocument.Parse(failing.DeleteTask(
            "root",
            "delete-failure",
            revision,
            "Root",
            cascade_children: false));

        Assert.AreEqual("operation_failed", result.RootElement.GetProperty("error").GetString());
        Assert.IsTrue(vault.Exists("root"));
        Assert.AreEqual("Keep [[root]].", File.ReadAllText(pagePath));
    }

    private sealed class ThrowOnceFault(ResourceMutationFailurePoint point)
        : IResourceMutationFaultInjector
    {
        private bool _thrown;

        public void ThrowIfInjected(ResourceMutationFailurePoint candidate)
        {
            if (_thrown || candidate != point)
                return;
            _thrown = true;
            throw new IOException("Injected deletion failure.");
        }
    }
}
