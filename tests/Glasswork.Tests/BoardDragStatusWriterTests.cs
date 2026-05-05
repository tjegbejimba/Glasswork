using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Tests for the drag-to-change-status write coordinator used in Board view.
/// Verifies conflict detection, retry logic, and SelfWriteCoordinator registration.
/// </summary>
[TestClass]
public class BoardDragStatusWriterTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private SelfWriteCoordinator _selfWrite = null!;
    private BoardDragStatusWriter _writer = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-drag-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _selfWrite = new SelfWriteCoordinator();
        _writer = new BoardDragStatusWriter(_vault, _selfWrite);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task WriteStatusChange_SucceedsWithoutConflict()
    {
        var task = new GlassworkTask
        {
            Id = "drag-card",
            Title = "Drag Card",
            Status = GlassworkTask.Statuses.Todo,
        };
        _vault.Save(task);

        var result = await _writer.TryWriteStatusChange(task, GlassworkTask.Statuses.InProgress);

        Assert.IsTrue(result.Success, "Write should succeed without conflict");
        Assert.IsNull(result.ErrorMessage);
        
        var loaded = _vault.Load("drag-card")!;
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, loaded.Status);
    }

    [TestMethod]
    public async Task WriteStatusChange_RegistersWithSelfWriteCoordinator()
    {
        var task = new GlassworkTask { Id = "register-me", Title = "Register", Status = GlassworkTask.Statuses.Todo };
        _vault.Save(task);

        var taskPath = Path.Combine(_tempDir, "register-me.md");

        await _writer.TryWriteStatusChange(task, GlassworkTask.Statuses.InProgress);

        // Self-write coordinator should suppress this path for the TTL window
        Assert.IsTrue(_selfWrite.IsSuppressed(taskPath), 
            "Write should be registered with SelfWriteCoordinator");
    }

    [TestMethod]
    public async Task WriteStatusChange_RetriesOnFirstConflict()
    {
        var task = new GlassworkTask { Id = "conflict-once", Title = "Conflict", Status = GlassworkTask.Statuses.Todo };
        _vault.Save(task);

        // Simulate external change after drag starts
        await Task.Delay(10);
        var external = _vault.Load("conflict-once")!;
        external.Description = "Changed externally";
        _vault.Save(external);

        var result = await _writer.TryWriteStatusChange(task, GlassworkTask.Statuses.InProgress);

        Assert.IsTrue(result.Success, "Should succeed after retry");
        
        var loaded = _vault.Load("conflict-once")!;
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, loaded.Status);
        Assert.AreEqual("Changed externally", loaded.Description, "Retry should preserve external changes");
    }

    [TestMethod]
    public async Task WriteStatusChange_AbortsOnSecondConflict()
    {
        var task = new GlassworkTask { Id = "conflict-twice", Title = "Conflict", Status = GlassworkTask.Statuses.Todo };
        _vault.Save(task);

        // Force two consecutive conflicts by intercepting the retry
        var attemptCount = 0;
        _writer.OnBeforeWrite = () =>
        {
            attemptCount++;
            if (attemptCount <= 2)
            {
                var ext = _vault.Load("conflict-twice")!;
                ext.Description = $"Change {attemptCount}";
                _vault.Save(ext);
            }
        };

        var result = await _writer.TryWriteStatusChange(task, GlassworkTask.Statuses.InProgress);

        Assert.IsFalse(result.Success, "Should fail after second conflict");
        Assert.IsNotNull(result.ErrorMessage);
        Assert.IsTrue(result.ErrorMessage!.Contains("changed externally"));
        
        // Original status should be unchanged
        var loaded = _vault.Load("conflict-twice")!;
        Assert.AreEqual(GlassworkTask.Statuses.Todo, loaded.Status);
    }
}
