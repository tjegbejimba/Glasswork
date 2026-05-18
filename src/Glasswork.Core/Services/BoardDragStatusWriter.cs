using System;
using System.IO;
using System.Threading.Tasks;
using Glasswork.Core.Models;

namespace Glasswork.Core.Services;

/// <summary>
/// Coordinates drag-to-change-status writes in Board view with conflict detection
/// and retry logic. Registers writes with SelfWriteCoordinator to prevent spurious
/// file-watcher events.
/// </summary>
public class BoardDragStatusWriter
{
    private readonly VaultService _vault;
    private readonly SelfWriteCoordinator _selfWrite;

    /// <summary>
    /// Test hook: called before each write attempt to simulate concurrent external changes.
    /// </summary>
    public Action? OnBeforeWrite { get; set; }

    public BoardDragStatusWriter(VaultService vault, SelfWriteCoordinator selfWrite)
    {
        _vault = vault;
        _selfWrite = selfWrite;
    }

    /// <summary>
    /// Attempts to write a status change with read-modify-write conflict detection.
    /// Retries once on mtime conflict; second conflict aborts.
    /// </summary>
    public async Task<DragWriteResult> TryWriteStatusChange(GlassworkTask task, string newStatus)
    {
        var filePath = Path.Combine(_vault.VaultPath, $"{task.Id}.md");
        
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var mtimeBefore = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;

            // Test hook for simulating external changes
            OnBeforeWrite?.Invoke();

            // Re-read the task to get latest state
            var fresh = _vault.Load(task.Id);
            if (fresh is null)
            {
                return DragWriteResult.Failure("Task file no longer exists");
            }

            // Check mtime conflict
            var mtimeAfter = File.GetLastWriteTimeUtc(filePath);
            if (mtimeAfter != mtimeBefore)
            {
                if (attempt == 1)
                {
                    // Second conflict - abort
                    return DragWriteResult.Failure("Task changed externally — please retry");
                }
                // First conflict - retry
                await Task.Delay(10);
                continue;
            }

            // No conflict - proceed with write
            fresh.Status = newStatus;
            
            // Register with coordinator before writing
            _selfWrite.RegisterWrite(filePath);
            
            _vault.Save(fresh);
            return DragWriteResult.Ok();
        }

        return DragWriteResult.Failure("Unexpected retry exhaustion");
    }
}

/// <summary>
/// Result of a drag-drop status write operation.
/// </summary>
public sealed class DragWriteResult
{
    public bool Success { get; }
    public string? ErrorMessage { get; }

    private DragWriteResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }

    public static DragWriteResult Ok() => new(true, null);
    public static DragWriteResult Failure(string message) => new(false, message);
}
