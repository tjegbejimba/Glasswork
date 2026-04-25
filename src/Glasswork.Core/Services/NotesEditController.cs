namespace Glasswork.Core.Services;

public enum NotesEditMode
{
    Read,
    Edit,
}

public sealed class NotesEditController
{
    private string _baseline;
    private string _buffer;

    public NotesEditController(string? baseline)
    {
        _baseline = baseline ?? string.Empty;
        _buffer = _baseline;
        Mode = NotesEditMode.Read;
    }

    public NotesEditMode Mode { get; private set; }

    public string Baseline => _baseline;

    public string Buffer => _buffer;

    public event EventHandler<NotesEditMode>? ModeChanged;

    public void EnterEdit()
    {
        if (Mode == NotesEditMode.Edit) return;
        _buffer = _baseline;
        Mode = NotesEditMode.Edit;
        ModeChanged?.Invoke(this, Mode);
    }

    public void UpdateBuffer(string? value)
    {
        _buffer = value ?? string.Empty;
    }

    public string Done()
    {
        if (Mode == NotesEditMode.Read) return _baseline;
        _baseline = _buffer;
        Mode = NotesEditMode.Read;
        ModeChanged?.Invoke(this, Mode);
        return _baseline;
    }

    public string Cancel()
    {
        if (Mode == NotesEditMode.Read) return _baseline;
        _buffer = _baseline;
        Mode = NotesEditMode.Read;
        ModeChanged?.Invoke(this, Mode);
        return _baseline;
    }

    public void OnExternalSave(string? newDiskValue)
    {
        _baseline = newDiskValue ?? string.Empty;
    }

    /// <summary>
    /// Classify how an external (agent / Obsidian) change to the Notes section
    /// should be reflected in the UI without losing in-flight user edits.
    /// See ADR 0006 §5 / issue #79.
    /// </summary>
    public NotesExternalChangeAction ClassifyExternalChange(string? newDiskValue)
    {
        var disk = newDiskValue ?? string.Empty;
        if (string.Equals(disk, _baseline, StringComparison.Ordinal))
            return NotesExternalChangeAction.Ignore;

        // Read mode (or edit mode with a pristine buffer): the user has no
        // typing to lose, so silently adopt the disk content.
        if (Mode == NotesEditMode.Read || string.Equals(_buffer, _baseline, StringComparison.Ordinal))
            return NotesExternalChangeAction.SilentRefresh;

        return NotesExternalChangeAction.Conflict;
    }

    /// <summary>
    /// Adopt the disk content silently. In edit mode the buffer also follows
    /// disk (only safe when the buffer was pristine — callers should have
    /// classified <see cref="NotesExternalChangeAction.SilentRefresh"/> first).
    /// </summary>
    public void ApplySilentRefresh(string? newDiskValue)
    {
        _baseline = newDiskValue ?? string.Empty;
        if (Mode == NotesEditMode.Edit)
            _buffer = _baseline;
    }

    /// <summary>
    /// Discard the user's in-flight buffer, replace it with disk content, and
    /// transition back to read mode. Wired to the conflict banner's
    /// "Discard mine and reload" button.
    /// </summary>
    public string ApplyDiscardAndReload(string? newDiskValue)
    {
        _baseline = newDiskValue ?? string.Empty;
        _buffer = _baseline;
        if (Mode != NotesEditMode.Read)
        {
            Mode = NotesEditMode.Read;
            ModeChanged?.Invoke(this, Mode);
        }
        return _baseline;
    }

    /// <summary>
    /// Snap the on-disk-at-edit-start baseline to the new disk content while
    /// preserving the user's buffer and edit mode. Wired to the conflict
    /// banner's "Keep mine and overwrite on save" button — the next Save will
    /// overwrite disk and the now-equal baseline prevents the conflict from
    /// re-firing on a subsequent watcher tick reading the same content.
    /// </summary>
    public void ApplyKeepAndOverwrite(string? newDiskValue)
    {
        _baseline = newDiskValue ?? string.Empty;
    }
}

public enum NotesExternalChangeAction
{
    Ignore,
    SilentRefresh,
    Conflict,
}
