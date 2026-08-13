using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

public enum TaskEditSaveResult
{
    Saved,
    Conflict,
    Missing,
}

public sealed class TaskEditSaveController(VaultService vault)
{
    private readonly VaultService _vault = vault ?? throw new ArgumentNullException(nameof(vault));

    public TaskEditSaveResult Save(GlassworkTask task)
    {
        try
        {
            _vault.Save(task);
            return TaskEditSaveResult.Saved;
        }
        catch (ResourceRevisionConflictException)
        {
            return TaskEditSaveResult.Conflict;
        }
    }

    public TaskEditSaveResult Overwrite(GlassworkTask task)
    {
        var current = _vault.Load(task.Id);
        if (current is null)
            return TaskEditSaveResult.Missing;

        task.ResourceRevision = current.ResourceRevision;
        return Save(task);
    }
}
