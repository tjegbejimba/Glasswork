using Glasswork.Core.Models;

namespace Glasswork.ViewModels;

/// <summary>
/// Tracks the state needed to undo a mark-done action on the Backlog board.
/// Captures the task ID and its previous status (todo or in_progress) at the
/// moment of mark-done, allowing restoration if the user clicks Undo.
/// </summary>
public class BacklogUndoState
{
    public bool HasUndo { get; private set; }
    public string? TaskId { get; private set; }
    public string? PreviousStatus { get; private set; }

    public void CaptureMarkDone(GlassworkTask task)
    {
        TaskId = task.Id;
        PreviousStatus = task.Status;
        HasUndo = true;
    }

    public void Clear()
    {
        TaskId = null;
        PreviousStatus = null;
        HasUndo = false;
    }
}
