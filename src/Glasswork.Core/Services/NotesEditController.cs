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
}
