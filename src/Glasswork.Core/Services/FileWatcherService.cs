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

    public event EventHandler<string>? TaskFileChanged;

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

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += (s, e) => RaiseEvent(e.FullPath);
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Stop() => _watcher.EnableRaisingEvents = false;

    public bool IsWatching => _watcher.EnableRaisingEvents;

    private void OnFileEvent(object sender, FileSystemEventArgs e) => RaiseEvent(e.FullPath);

    private void RaiseEvent(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        // Skip index/schema files
        if (fileName.StartsWith("_")) return;

        // Skip events caused by our own writes (e.g. VaultService.Save) — otherwise
        // every Field_LostFocus → Save round-trips into a false-positive reload banner.
        if (_selfWrites?.IsSuppressed(fullPath) == true) return;

        TaskFileChanged?.Invoke(this, fileName);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
