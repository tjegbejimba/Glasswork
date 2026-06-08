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
    /// UI state flag key prefix. The full key is vault-scoped:
    /// "migration.myDayDateScoped.{vault-path-hash}".
    /// When true, the migration has already run for that vault and must not run again.
    /// </summary>
    public const string MigrationFlagKeyPrefix = "migration.myDayDateScoped";

    /// <summary>
    /// Apply the one-time my_day date-scoped migration if the flag is unset.
    /// For each task with my_day &lt; today, rewrites my_day = today.
    /// Sets the flag after completion so the migration never runs twice.
    /// Vault-scoped: switching vaults runs the migration independently for each vault.
    /// </summary>
    /// <param name="vault">Vault service for loading/saving tasks.</param>
    /// <param name="uiState">UI state service for the idempotency flag.</param>
    /// <param name="today">Current date (today).</param>
    public static void ApplyMigration(VaultService vault, IUiStateService uiState, DateOnly today)
    {
        // Vault-scoped flag key: hash the vault path to make the flag unique per vault
        var vaultHash = Math.Abs(vault.VaultPath.GetHashCode()).ToString("X8");
        var flagKey = $"{MigrationFlagKeyPrefix}.{vaultHash}";

        // Idempotency guard: if flag is set, migration already ran for this vault — do nothing
        if (uiState.Get<bool>(flagKey))
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

        // Set the flag so migration never runs again for this vault
        uiState.Set(flagKey, true);
    }
}
