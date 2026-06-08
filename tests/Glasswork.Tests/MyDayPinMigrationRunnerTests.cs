using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class MyDayPinMigrationRunnerTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    [TestMethod]
    public void ApplyMigration_FlagUnset_RewritesPastPins()
    {
        // Arrange: create a temp vault with past-dated pins
        var tempVault = Path.Combine(Path.GetTempPath(), $"glasswork-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempVault);
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var taskPath = Path.Combine(tempVault, "past-pin.md");
            File.WriteAllText(taskPath, $@"---
id: past-pin
title: Past pin
my_day: {yesterday:yyyy-MM-dd}
---
");

            var selfWrites = new SelfWriteCoordinator(tempVault);
            var vault = new VaultService(tempVault, selfWrites);
            var uiState = new InMemoryUiStateService();

            // Act: apply migration
            MyDayPinMigrationRunner.ApplyMigration(vault, uiState, Today);

            // Assert: task now has my_day = today
            var reloaded = vault.Load("past-pin");
            Assert.IsNotNull(reloaded);
            Assert.IsNotNull(reloaded.MyDay);
            Assert.AreEqual(Today, DateOnly.FromDateTime(reloaded.MyDay.Value.Date));

            // Assert: flag is set (vault-scoped)
            var vaultHash = Math.Abs(vault.VaultPath.GetHashCode()).ToString("X8");
            var expectedFlagKey = $"{MyDayPinMigrationRunner.MigrationFlagKeyPrefix}.{vaultHash}";
            Assert.IsTrue(uiState.Get<bool>(expectedFlagKey));
        }
        finally
        {
            if (Directory.Exists(tempVault))
                Directory.Delete(tempVault, recursive: true);
        }
    }

    [TestMethod]
    public void ApplyMigration_FlagSet_Idempotent()
    {
        // Arrange: create a temp vault with past-dated pin
        var tempVault = Path.Combine(Path.GetTempPath(), $"glasswork-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempVault);
        try
        {
            var twoDaysAgo = DateTime.Today.AddDays(-2);
            var taskPath = Path.Combine(tempVault, "past-pin.md");
            File.WriteAllText(taskPath, $@"---
id: past-pin
title: Past pin
my_day: {twoDaysAgo:yyyy-MM-dd}
---
");

            var selfWrites = new SelfWriteCoordinator(tempVault);
            var vault = new VaultService(tempVault, selfWrites);
            var uiState = new InMemoryUiStateService();

            // First pass: migration runs
            MyDayPinMigrationRunner.ApplyMigration(vault, uiState, Today);
            var afterFirst = vault.Load("past-pin");
            Assert.AreEqual(Today, DateOnly.FromDateTime(afterFirst!.MyDay!.Value.Date));

            // Manually reset my_day to simulate "yesterday" after migration
            var task = vault.Load("past-pin");
            task!.MyDay = DateTime.Today.AddDays(-1);
            vault.Save(task);
            var afterManualReset = vault.Load("past-pin");
            Assert.AreEqual(Today.AddDays(-1), DateOnly.FromDateTime(afterManualReset!.MyDay!.Value.Date));

            // Act: second pass with flag already set
            MyDayPinMigrationRunner.ApplyMigration(vault, uiState, Today);

            // Assert: my_day is still yesterday (migration did NOT re-run)
            var afterSecond = vault.Load("past-pin");
            Assert.AreEqual(Today.AddDays(-1), DateOnly.FromDateTime(afterSecond!.MyDay!.Value.Date));
        }
        finally
        {
            if (Directory.Exists(tempVault))
                Directory.Delete(tempVault, recursive: true);
        }
    }

    [TestMethod]
    public void ApplyMigration_TodayAndFuturePins_Unchanged()
    {
        // Arrange: create a temp vault with today and future pins
        var tempVault = Path.Combine(Path.GetTempPath(), $"glasswork-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempVault);
        try
        {
            var todayPath = Path.Combine(tempVault, "today-pin.md");
            File.WriteAllText(todayPath, $@"---
id: today-pin
title: Today pin
my_day: {DateTime.Today:yyyy-MM-dd}
---
");

            var tomorrow = DateTime.Today.AddDays(1);
            var futurePath = Path.Combine(tempVault, "future-pin.md");
            File.WriteAllText(futurePath, $@"---
id: future-pin
title: Future pin
my_day: {tomorrow:yyyy-MM-dd}
---
");

            var selfWrites = new SelfWriteCoordinator(tempVault);
            var vault = new VaultService(tempVault, selfWrites);
            var uiState = new InMemoryUiStateService();

            // Act: apply migration
            MyDayPinMigrationRunner.ApplyMigration(vault, uiState, Today);

            // Assert: today pin unchanged
            var todayTask = vault.Load("today-pin");
            Assert.AreEqual(Today, DateOnly.FromDateTime(todayTask!.MyDay!.Value.Date));

            // Assert: future pin unchanged
            var futureTask = vault.Load("future-pin");
            Assert.AreEqual(Today.AddDays(1), DateOnly.FromDateTime(futureTask!.MyDay!.Value.Date));
        }
        finally
        {
            if (Directory.Exists(tempVault))
                Directory.Delete(tempVault, recursive: true);
        }
    }

    [TestMethod]
    public void ApplyMigration_TwoVaults_IndependentFlags()
    {
        // Regression test for GPT code review finding: vault-scoped flags
        // Arrange: create two temp vaults with past-dated pins
        var vault1Path = Path.Combine(Path.GetTempPath(), $"glasswork-test-{Guid.NewGuid()}");
        var vault2Path = Path.Combine(Path.GetTempPath(), $"glasswork-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(vault1Path);
        Directory.CreateDirectory(vault2Path);
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);

            // Vault 1: past-dated pin
            var task1Path = Path.Combine(vault1Path, "past-pin.md");
            File.WriteAllText(task1Path, $@"---
id: past-pin
title: Past pin
my_day: {yesterday:yyyy-MM-dd}
---
");

            // Vault 2: past-dated pin
            var task2Path = Path.Combine(vault2Path, "past-pin.md");
            File.WriteAllText(task2Path, $@"---
id: past-pin
title: Past pin
my_day: {yesterday:yyyy-MM-dd}
---
");

            var vault1 = new VaultService(vault1Path, new SelfWriteCoordinator(vault1Path));
            var vault2 = new VaultService(vault2Path, new SelfWriteCoordinator(vault2Path));
            var sharedUiState = new InMemoryUiStateService(); // same ui-state, different vaults

            // Act: migrate vault1
            MyDayPinMigrationRunner.ApplyMigration(vault1, sharedUiState, Today);

            // Assert: vault1 migrated
            var vault1Task = vault1.Load("past-pin");
            Assert.AreEqual(Today, DateOnly.FromDateTime(vault1Task!.MyDay!.Value.Date));

            // Assert: vault2 NOT migrated yet (different vault, independent flag)
            var vault2TaskBefore = vault2.Load("past-pin");
            Assert.AreEqual(Today.AddDays(-1), DateOnly.FromDateTime(vault2TaskBefore!.MyDay!.Value.Date));

            // Act: migrate vault2
            MyDayPinMigrationRunner.ApplyMigration(vault2, sharedUiState, Today);

            // Assert: vault2 now migrated
            var vault2TaskAfter = vault2.Load("past-pin");
            Assert.AreEqual(Today, DateOnly.FromDateTime(vault2TaskAfter!.MyDay!.Value.Date));
        }
        finally
        {
            if (Directory.Exists(vault1Path))
                Directory.Delete(vault1Path, recursive: true);
            if (Directory.Exists(vault2Path))
                Directory.Delete(vault2Path, recursive: true);
        }
    }

    /// <summary>
    /// In-memory UI state service for testing. Does not persist.
    /// </summary>
    private class InMemoryUiStateService : IUiStateService
    {
        private readonly Dictionary<string, object> _data = new();

        public T? Get<T>(string key)
        {
            return _data.TryGetValue(key, out var value) && value is T typed
                ? typed
                : default;
        }

        public void Set<T>(string key, T value)
        {
            if (value is null)
                _data.Remove(key);
            else
                _data[key] = value;
        }

        public void Remove(string key) => _data.Remove(key);

        public void Save() { /* no-op: in-memory only */ }

        public void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes)
        {
            var toRemove = _data.Keys
                .Where(k => k.StartsWith(keyPrefix) && !liveSuffixes.Contains(k.Substring(keyPrefix.Length)))
                .ToList();
            foreach (var key in toRemove)
                _data.Remove(key);
        }
    }
}
