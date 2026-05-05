using Glasswork.Core.Models;
using Glasswork.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class BacklogUndoStateTests
{
    [TestMethod]
    public void CaptureMarkDone_StoresPreviousStatus_WhenTaskWasTodo()
    {
        // Arrange
        var state = new BacklogUndoState();
        var task = new GlassworkTask { Id = "task-1", Status = GlassworkTask.Statuses.Todo };

        // Act
        state.CaptureMarkDone(task);

        // Assert
        Assert.IsTrue(state.HasUndo);
        Assert.AreEqual("task-1", state.TaskId);
        Assert.AreEqual(GlassworkTask.Statuses.Todo, state.PreviousStatus);
    }

    [TestMethod]
    public void CaptureMarkDone_StoresPreviousStatus_WhenTaskWasInProgress()
    {
        // Arrange
        var state = new BacklogUndoState();
        var task = new GlassworkTask { Id = "task-2", Status = GlassworkTask.Statuses.InProgress };

        // Act
        state.CaptureMarkDone(task);

        // Assert
        Assert.IsTrue(state.HasUndo);
        Assert.AreEqual("task-2", state.TaskId);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, state.PreviousStatus);
    }

    [TestMethod]
    public void Clear_RemovesUndoState()
    {
        // Arrange
        var state = new BacklogUndoState();
        var task = new GlassworkTask { Id = "task-1", Status = GlassworkTask.Statuses.Todo };
        state.CaptureMarkDone(task);

        // Act
        state.Clear();

        // Assert
        Assert.IsFalse(state.HasUndo);
        Assert.IsNull(state.TaskId);
        Assert.IsNull(state.PreviousStatus);
    }

    [TestMethod]
    public void CaptureMarkDone_ReplacesExistingState()
    {
        // Arrange
        var state = new BacklogUndoState();
        var task1 = new GlassworkTask { Id = "task-1", Status = GlassworkTask.Statuses.Todo };
        var task2 = new GlassworkTask { Id = "task-2", Status = GlassworkTask.Statuses.InProgress };
        
        state.CaptureMarkDone(task1);

        // Act - second mark-done replaces first
        state.CaptureMarkDone(task2);

        // Assert - only task-2 is stored
        Assert.IsTrue(state.HasUndo);
        Assert.AreEqual("task-2", state.TaskId);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, state.PreviousStatus);
    }
}
