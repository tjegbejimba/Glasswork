using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Tests for the domain events <see cref="VaultService.TaskWritten"/> and
/// <see cref="VaultService.TaskDeleted"/> introduced for issue #184. The
/// in-memory <see cref="IndexService"/> subscribes to these so the canonical
/// snapshot store stays consistent with successful disk writes without going
/// through a watcher round-trip.
/// </summary>
[TestClass]
public class VaultServiceEventsTests
{
    private string _tempDir = null!;
    private VaultService _vault = null!;
    private List<string> _written = null!;
    private List<string> _deleted = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "glasswork-vse-" + Guid.NewGuid().ToString("N")[..8]);
        _vault = new VaultService(_tempDir);
        _written = new List<string>();
        _deleted = new List<string>();
        _vault.TaskWritten += (_, id) => _written.Add(id);
        _vault.TaskDeleted += (_, id) => _deleted.Add(id);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Save_RaisesTaskWritten_WithTaskId()
    {
        var t = new GlassworkTask { Id = "t1", Title = "T1" };
        _vault.Save(t);

        CollectionAssert.AreEqual(new[] { "t1" }, _written);
    }

    [TestMethod]
    public void Delete_RaisesTaskDeleted_WithTaskId()
    {
        _vault.Save(new GlassworkTask { Id = "t1", Title = "T1" });
        _written.Clear();

        var deleted = _vault.Delete("t1");

        Assert.IsTrue(deleted);
        CollectionAssert.AreEqual(new[] { "t1" }, _deleted);
    }

    [TestMethod]
    public void Delete_NonexistentFile_DoesNotRaiseEvent()
    {
        _vault.Delete("ghost");

        Assert.AreEqual(0, _deleted.Count);
    }

    [TestMethod]
    public void UpdateSubtaskCheckbox_RaisesTaskWritten()
    {
        var t = new GlassworkTask { Id = "t1", Title = "T1" };
        t.Subtasks.Add(new SubTask { Text = "step", IsCompleted = false });
        _vault.Save(t);
        _written.Clear();

        _vault.UpdateSubtaskCheckbox("t1", "step", isCompleted: true);

        CollectionAssert.AreEqual(new[] { "t1" }, _written);
    }

    [TestMethod]
    public void UpdateSubtaskCheckbox_NoOp_DoesNotRaiseEvent()
    {
        // Subtask title does not exist — UpdateSubtaskCheckbox is a no-op and
        // must not raise the event.
        _vault.Save(new GlassworkTask { Id = "t1", Title = "T1" });
        _written.Clear();

        _vault.UpdateSubtaskCheckbox("t1", "nonexistent", isCompleted: true);

        Assert.AreEqual(0, _written.Count);
    }

    [TestMethod]
    public void SetSubtaskMyDay_RaisesTaskWritten()
    {
        var t = new GlassworkTask { Id = "t1", Title = "T1" };
        t.Subtasks.Add(new SubTask { Text = "step" });
        _vault.Save(t);
        _written.Clear();

        _vault.SetSubtaskMyDay("t1", "step", isMyDay: true);

        CollectionAssert.AreEqual(new[] { "t1" }, _written);
    }

    [TestMethod]
    public void SetSubtaskDue_RaisesTaskWritten()
    {
        var t = new GlassworkTask { Id = "t1", Title = "T1" };
        t.Subtasks.Add(new SubTask { Text = "step" });
        _vault.Save(t);
        _written.Clear();

        _vault.SetSubtaskDue("t1", "step", new DateTime(2026, 1, 1));

        CollectionAssert.AreEqual(new[] { "t1" }, _written);
    }

    [TestMethod]
    public void SetAdoLink_RaisesTaskWritten()
    {
        _vault.Save(new GlassworkTask { Id = "t1", Title = "T1" });
        _written.Clear();

        _vault.SetAdoLink("t1", 1234, "ADO");

        CollectionAssert.AreEqual(new[] { "t1" }, _written);
    }

    [TestMethod]
    public void SetParent_RaisesTaskWritten()
    {
        _vault.Save(new GlassworkTask { Id = "t1", Title = "T1" });
        _written.Clear();

        _vault.SetParent("t1", "parent-id");

        CollectionAssert.AreEqual(new[] { "t1" }, _written);
    }

    [TestMethod]
    public void AddSubtask_RaisesTaskWritten()
    {
        _vault.Save(new GlassworkTask { Id = "t1", Title = "T1" });
        _written.Clear();

        _vault.AddSubtask("t1", "new step");

        CollectionAssert.AreEqual(new[] { "t1" }, _written);
    }

    [TestMethod]
    public void Save_DoesNotCorruptOnEventHandlerException()
    {
        // A subscriber throwing must not prevent persistence from completing
        // (Save already returned by then), nor should the unhandled exception
        // surface to the caller — events are best-effort/log-and-continue.
        _vault.TaskWritten += (_, _) => throw new InvalidOperationException("boom");

        _vault.Save(new GlassworkTask { Id = "t1", Title = "T1" });

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "t1.md")));
    }
}
