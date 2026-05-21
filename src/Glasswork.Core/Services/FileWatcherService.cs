using System;
using System.IO;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Watches the vault directory for external changes (agent or Obsidian edits)
/// and raises events so the UI can refresh.
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _vaultPath;
    private readonly SelfWriteCoordinator? _selfWrites;

    /// <summary>
    /// Legacy filename-only event. Fires for create/change/delete/rename of any
    /// non-underscore <c>*.md</c> file in the vault root that is not currently
    /// suppressed by the <see cref="SelfWriteCoordinator"/>. New code should
    /// prefer <see cref="TaskFileChange"/> which carries kind + old name.
    /// </summary>
    public event EventHandler<string>? TaskFileChanged;

    /// <summary>
    /// Typed change event (issue #184): carries <see cref="TaskFileChangeKind"/>
    /// and, for renames, the prior filename. Fires alongside
    /// <see cref="TaskFileChanged"/> under the same suppression rules.
    /// </summary>
    public event EventHandler<TaskFileChange>? TaskFileChange;

    public FileWatcherService(string vaultPath) : this(vaultPath, null) { }

    public FileWatcherService(string vaultPath, SelfWriteCoordinator? selfWrites)
    {
        _vaultPath = vaultPath;
        _selfWrites = selfWrites;

        if (!Directory.Exists(vaultPath))
            Directory.CreateDirectory(vaultPath);

        _watcher = new FileSystemWatcher(vaultPath, "*.md")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = false
        };

        _watcher.Changed += (s, e) => RaiseEvent(TaskFileChangeKind.CreatedOrChanged, oldPath: null, e.FullPath);
        _watcher.Created += (s, e) => RaiseEvent(TaskFileChangeKind.CreatedOrChanged, oldPath: null, e.FullPath);
        _watcher.Deleted += (s, e) => RaiseEvent(TaskFileChangeKind.Deleted, oldPath: null, e.FullPath);
        _watcher.Renamed += (s, e) => RaiseEvent(TaskFileChangeKind.Renamed, e.OldFullPath, e.FullPath);
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Stop() => _watcher.EnableRaisingEvents = false;

    public bool IsWatching => _watcher.EnableRaisingEvents;

    private void RaiseEvent(TaskFileChangeKind kind, string? oldPath, string newPath)
    {
        var newFileName = Path.GetFileName(newPath);
        // Skip index/schema files
        if (newFileName.StartsWith('_')) return;

        var oldFileName = oldPath is null ? null : Path.GetFileName(oldPath);

        // Two suppression rules — see ADR 0010 §"SelfWriteCoordinator split":
        //
        //  * IsOwnProcessWrite — only same-process VaultService.Save echoes. Used
        //    to gate the typed TaskFileChange event so the IndexService still sees
        //    cross-process MCP edits and refreshes the UI via the watcher path.
        //
        //  * IsSuppressed — same-process AND cross-process (marker-file) writes.
        //    Used to gate the legacy TaskFileChanged event whose downstream
        //    consumers (conflict banner, "external edit" reload prompt) must NOT
        //    fire for coordinated MCP writes.
        var isOwn = _selfWrites?.IsOwnProcessWrite(newPath) == true;
        var isSuppressed = _selfWrites?.IsSuppressed(newPath) == true;

        if (!isSuppressed)
            TaskFileChanged?.Invoke(this, newFileName);

        if (!isOwn)
            TaskFileChange?.Invoke(this, new TaskFileChange(kind, oldFileName, newFileName));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
