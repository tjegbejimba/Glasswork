using System;
using System.Collections.Generic;
using System.IO;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class VaultSetLinksTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private SelfWriteCoordinator _selfWrites = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-setlinks-" + Guid.NewGuid().ToString("N")[..8]);
        _selfWrites = new SelfWriteCoordinator();
        _vault = new VaultService(_tempDir, _selfWrites);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void SetLinks_PersistsLinksToFrontmatter()
    {
        var taskId = "set-links-basic";
        var original =
            "---\n" +
            "id: set-links-basic\n" +
            "title: T\n" +
            "status: todo\n" +
            "priority: medium\n" +
            "created: 2024-01-01\n" +
            "---\n" +
            "\n" +
            "Body\n";
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), original);

        _vault.SetLinks(taskId, new List<TaskLink>
        {
            new() { Type = TaskLink.Types.Pr, Value = "https://github.com/org/repo/pull/42" },
            new() { Type = TaskLink.Types.Other, Value = "https://example.com", Label = "Ref" }
        });

        var task = _vault.Load(taskId)!;
        Assert.HasCount(2, task.Links);
        Assert.AreEqual(TaskLink.Types.Pr, task.Links[0].Type);
        Assert.AreEqual("https://github.com/org/repo/pull/42", task.Links[0].Value);
        Assert.AreEqual(TaskLink.Types.Other, task.Links[1].Type);
        Assert.AreEqual("https://example.com", task.Links[1].Value);
        Assert.AreEqual("Ref", task.Links[1].Label);
    }

    [TestMethod]
    public void SetLinks_EmptyList_ClearsLinks()
    {
        var taskId = "set-links-clear";
        var original =
            "---\n" +
            "id: set-links-clear\n" +
            "title: T\n" +
            "status: todo\n" +
            "priority: medium\n" +
            "created: 2024-01-01\n" +
            "links:\n" +
            "- type: pr\n" +
            "  value: https://github.com/org/repo/pull/1\n" +
            "---\n" +
            "\n" +
            "Body\n";
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), original);

        _vault.SetLinks(taskId, new List<TaskLink>());

        var task = _vault.Load(taskId)!;
        Assert.IsEmpty(task.Links);
    }

    [TestMethod]
    public void SetLinks_ReplacesExistingLinks()
    {
        var taskId = "set-links-replace";
        var original =
            "---\n" +
            "id: set-links-replace\n" +
            "title: T\n" +
            "status: todo\n" +
            "priority: medium\n" +
            "created: 2024-01-01\n" +
            "links:\n" +
            "- type: pr\n" +
            "  value: https://github.com/old\n" +
            "---\n" +
            "\n" +
            "Body\n";
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), original);

        _vault.SetLinks(taskId, new List<TaskLink>
        {
            new() { Type = TaskLink.Types.Build, Value = "https://dev.azure.com/build/456" }
        });

        var task = _vault.Load(taskId)!;
        Assert.HasCount(1, task.Links);
        Assert.AreEqual(TaskLink.Types.Build, task.Links[0].Type);
        Assert.AreEqual("https://dev.azure.com/build/456", task.Links[0].Value);
    }

    [TestMethod]
    public void SetLinks_NonExistentTask_IsNoOp()
    {
        // Should not throw
        _vault.SetLinks("nonexistent", new List<TaskLink>());
    }

    [TestMethod]
    public void SetLinks_AllTypes_RoundTrip()
    {
        var taskId = "set-links-alltypes";
        var original =
            "---\n" +
            "id: set-links-alltypes\n" +
            "title: T\n" +
            "status: todo\n" +
            "priority: medium\n" +
            "created: 2024-01-01\n" +
            "---\n" +
            "\n" +
            "Body\n";
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), original);

        var links = new List<TaskLink>
        {
            new() { Type = TaskLink.Types.Ado, Value = "1234", Label = "My ADO item" },
            new() { Type = TaskLink.Types.Pr, Value = "https://github.com/org/repo/pull/5" },
            new() { Type = TaskLink.Types.Incident, Value = "ICM 965114" },
            new() { Type = TaskLink.Types.Doc, Value = "https://eng.ms/docs/example" },
            new() { Type = TaskLink.Types.Build, Value = "https://dev.azure.com/_build/123" },
            new() { Type = TaskLink.Types.Other, Value = "https://example.com" },
        };

        _vault.SetLinks(taskId, links);

        var task = _vault.Load(taskId)!;
        Assert.HasCount(6, task.Links);
        Assert.AreEqual(TaskLink.Types.Ado, task.Links[0].Type);
        Assert.AreEqual("My ADO item", task.Links[0].Label);
        Assert.AreEqual(TaskLink.Types.Pr, task.Links[1].Type);
        Assert.AreEqual(TaskLink.Types.Incident, task.Links[2].Type);
        Assert.AreEqual(TaskLink.Types.Doc, task.Links[3].Type);
        Assert.AreEqual(TaskLink.Types.Build, task.Links[4].Type);
        Assert.AreEqual(TaskLink.Types.Other, task.Links[5].Type);
    }

    [TestMethod]
    public void SetLinks_PreservesBodyAndNotes()
    {
        var taskId = "set-links-body";
        var original =
            "---\n" +
            "id: set-links-body\n" +
            "title: T\n" +
            "status: todo\n" +
            "priority: medium\n" +
            "created: 2024-01-01\n" +
            "---\n" +
            "\n" +
            "Description body.\n" +
            "\n" +
            "## Notes\n" +
            "\n" +
            "Some notes.\n" +
            "\n" +
            "## Related\n" +
            "\n" +
            "## Subtasks\n";
        File.WriteAllText(Path.Combine(_tempDir, $"{taskId}.md"), original);

        _vault.SetLinks(taskId, new List<TaskLink>
        {
            new() { Type = TaskLink.Types.Doc, Value = "https://docs.example.com" }
        });

        var task = _vault.Load(taskId)!;
        Assert.HasCount(1, task.Links);
        Assert.Contains("Description body.", task.Description, "Description should be preserved");
        Assert.Contains("Some notes.", task.Notes, "Notes should be preserved");
    }
}
