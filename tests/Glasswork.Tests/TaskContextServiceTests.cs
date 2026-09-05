using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskContextServiceTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private TaskContextService _contextService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-ctx-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _contextService = new TaskContextService(_vault);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void BuildContextBundle_ValidTask_ReturnsBasicFields()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "research-api",
            Title = "Research API integration",
            Status = GlassworkTask.Statuses.InProgress,
            Description = "## Context\n\nInvestigate third-party API options.",
            Notes = "Started with vendor docs.",
            Size = "future_bucket",
        };
        _vault.Save(task);

        // Act
        var bundle = _contextService.BuildContextBundle("research-api");

        // Assert
        Assert.IsNotNull(bundle);
        Assert.AreEqual("research-api", bundle.TaskId);
        Assert.AreEqual("Research API integration", bundle.Title);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, bundle.Status);
        Assert.AreEqual("## Context\n\nInvestigate third-party API options.", bundle.Description);
        Assert.AreEqual("Started with vendor docs.", bundle.Notes);
        Assert.AreEqual("future_bucket", bundle.Size);
        Assert.AreEqual(_vault.Load(task.Id)!.ResourceRevision, bundle.ResourceRevision);
        Assert.EndsWith("research-api.md", bundle.TaskFilePath);
    }

    [TestMethod]
    public void BuildContextBundle_IncludesActiveSubtasks()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "setup-ci",
            Title = "Set up CI pipeline",
            Status = GlassworkTask.Statuses.InProgress,
            Subtasks = new List<SubTask>
            {
                new() { Text = "Configure GitHub Actions", Status = "todo" },
                new() { Text = "Add test job", Status = "in_progress" },
                new() { Text = "Enable status checks", Status = "blocked" },
            }
        };
        _vault.Save(task);

        // Act
        var bundle = _contextService.BuildContextBundle("setup-ci");

        // Assert
        Assert.IsNotNull(bundle);
        Assert.HasCount(3, bundle.ActiveSubtasks);
        Assert.AreEqual("Configure GitHub Actions", bundle.ActiveSubtasks[0].Text);
        Assert.AreEqual("todo", bundle.ActiveSubtasks[0].Status);
        Assert.AreEqual("Add test job", bundle.ActiveSubtasks[1].Text);
        Assert.AreEqual("in_progress", bundle.ActiveSubtasks[1].Status);
        Assert.AreEqual("Enable status checks", bundle.ActiveSubtasks[2].Text);
        Assert.AreEqual("blocked", bundle.ActiveSubtasks[2].Status);
    }

    [TestMethod]
    public void BuildContextBundle_ExcludesDoneAndDroppedSubtasks()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "migration-task",
            Title = "Database migration",
            Status = GlassworkTask.Statuses.InProgress,
            Subtasks = new List<SubTask>
            {
                new() { Text = "Backup database", Status = "done" },
                new() { Text = "Run migration scripts", Status = "in_progress" },
                new() { Text = "Old approach", Status = "dropped" },
                new() { Text = "Verify data integrity", Status = "todo" },
            }
        };
        _vault.Save(task);

        // Act
        var bundle = _contextService.BuildContextBundle("migration-task");

        // Assert
        Assert.IsNotNull(bundle);
        Assert.HasCount(2, bundle.ActiveSubtasks);
        Assert.AreEqual("Run migration scripts", bundle.ActiveSubtasks[0].Text);
        Assert.AreEqual("Verify data integrity", bundle.ActiveSubtasks[1].Text);
    }

    [TestMethod]
    public void BuildContextBundle_IncludesOpenBlockers()
    {
        // Arrange
        var task = new GlassworkTask
        {
            Id = "deploy-app",
            Title = "Deploy application",
            Status = GlassworkTask.Statuses.InProgress,
            Subtasks = new List<SubTask>
            {
                new() { Text = "Run tests", Status = "blocked" },
                new() { Text = "Review changes", Status = "blocked" },
                new() { Text = "Deploy to prod", Status = "todo" },
            }
        };
        _vault.Save(task);

        // Act
        var bundle = _contextService.BuildContextBundle("deploy-app");

        // Assert
        Assert.IsNotNull(bundle);
        Assert.HasCount(2, bundle.OpenBlockers);
        Assert.AreEqual("Run tests", bundle.OpenBlockers[0].Text);
        Assert.AreEqual("blocked", bundle.OpenBlockers[0].Status);
        Assert.AreEqual("Review changes", bundle.OpenBlockers[1].Text);
        Assert.AreEqual("blocked", bundle.OpenBlockers[1].Status);
    }

    [TestMethod]
    public void BuildContextBundle_WithArtifactStore_IncludesArtifacts()
    {
        // Arrange
        // Create a proper vault structure: vaultRoot/wiki/todo/
        var vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-artifact-" + Guid.NewGuid().ToString("N")[..8]);
        var todoDir = Path.Combine(vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(todoDir);

        var vault = new VaultService(todoDir);
        var artifactStore = new FileSystemArtifactStore(vaultRoot);
        var contextService = new TaskContextService(vault, artifactStore);

        var task = new GlassworkTask
        {
            Id = "test-task",
            Title = "Test task",
            Status = GlassworkTask.Statuses.Todo,
        };
        vault.Save(task);

        // Create an artifact
        var artifactsDir = Path.Combine(todoDir, "test-task.artifacts");
        Directory.CreateDirectory(artifactsDir);
        File.WriteAllText(Path.Combine(artifactsDir, "plan.md"), "# Implementation Plan");

        // Act
        var bundle = contextService.BuildContextBundle("test-task");

        // Assert
        Assert.IsNotNull(bundle);
        Assert.HasCount(1, bundle.LatestArtifacts);
        Assert.AreEqual("Implementation Plan", bundle.LatestArtifacts[0].Title);
        Assert.IsNotNull(bundle.ArtifactsPath);
        Assert.EndsWith("test-task.artifacts", bundle.ArtifactsPath);

        // Cleanup
        if (Directory.Exists(vaultRoot))
            Directory.Delete(vaultRoot, recursive: true);
    }

    [TestMethod]
    public void BuildContextBundle_WithBacklinkIndex_IncludesBacklinks()
    {
        // Arrange
        // Create a proper vault structure: vaultRoot/wiki/todo/
        var vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-backlink-" + Guid.NewGuid().ToString("N")[..8]);
        var todoDir = Path.Combine(vaultRoot, "wiki", "todo");
        var conceptsDir = Path.Combine(vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(todoDir);
        Directory.CreateDirectory(conceptsDir);

        var vault = new VaultService(todoDir);

        var task = new GlassworkTask
        {
            Id = "api-task",
            Title = "API task",
            Status = GlassworkTask.Statuses.Todo,
        };
        vault.Save(task);

        // Create a backlink after the task exists
        File.WriteAllText(Path.Combine(conceptsDir, "api-design.md"), "See [[api-task]] for implementation.");

        var backlinkIndex = new BacklinkIndex();
        backlinkIndex.Build(vaultRoot);
        var contextService = new TaskContextService(vault, null, backlinkIndex);

        // Act
        var bundle = contextService.BuildContextBundle("api-task");

        // Assert
        Assert.IsNotNull(bundle);
        Assert.HasCount(1, bundle.Backlinks);
        Assert.AreEqual("api-design", bundle.Backlinks[0].LinkingPageTitle);
        Assert.AreEqual(BacklinkPageType.Concept, bundle.Backlinks[0].PageType);

        // Cleanup
        if (Directory.Exists(vaultRoot))
            Directory.Delete(vaultRoot, recursive: true);
    }
}
