using System;
using Glasswork.Core.Services;

namespace Glasswork.Core.Services;

/// <summary>
/// One-time migration runner for ADR 0013 (date-scoped My Day pins).
/// Rolls forward past-dated my_day pins to today so they aren't mass-evicted on upgrade.
/// Guarded by a ui-state flag — runs exactly once.
/// </summary>
public static class MyDayPinMigrationRunner
{
    /// <summary>
    /// UI state flag key. When true, the migration has already run and must not run again.
    /// </summary>
    public const string MigrationFlagKey = "migration.myDayDateScoped";

    /// <summary>
    /// Apply the one-time my_day date-scoped migration if the flag is unset.
    /// For each task with my_day &lt; today, rewrites my_day = today.
    /// Sets the flag after completion so the migration never runs twice.
    /// </summary>
    /// <param name="vault">Vault service for loading/saving tasks.</param>
    /// <param name="uiState">UI state service for the idempotency flag.</param>
    /// <param name="today">Current date (today).</param>
    public static void ApplyMigration(VaultService vault, IUiStateService uiState, DateOnly today)
    {
        // Idempotency guard: if flag is set, migration already ran — do nothing
        if (uiState.Get<bool>(MigrationFlagKey))
            return;

        // Load all tasks
        var allTasks = vault.LoadAll();

        // Find past-dated pins
        var pastPinIds = MyDayPinMigration.PinsToRollForward(allTasks, today);

        // Roll each past pin forward to today
        foreach (var taskId in pastPinIds)
        {
            var task = vault.Load(taskId);
            if (task is null) continue; // should never happen, but defensive
            task.MyDay = today.ToDateTime(TimeOnly.MinValue);
            vault.Save(task);
        }

        // Set the flag so migration never runs again
        uiState.Set(MigrationFlagKey, true);
    }
}
