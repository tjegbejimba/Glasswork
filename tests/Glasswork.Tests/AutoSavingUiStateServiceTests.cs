using System;
using System.Collections.Generic;
using Glasswork.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Glasswork.Tests;

[TestClass]
public class AutoSavingUiStateServiceTests
{
    [TestMethod]
    public void Set_SchedulesSave()
    {
        // Arrange
        var inner = new FakeUiStateService();
        var debouncer = new FakeDebouncer();
        var decorator = new AutoSavingUiStateService(inner, debouncer);
        
        // Act
        decorator.Set("key1", "value1");
        
        // Assert
        Assert.AreEqual(1, debouncer.TriggerCount, "Set should schedule a save");
        Assert.AreEqual("value1", inner.Get<string>("key1"), "Set should update inner state");
    }
    
    [TestMethod]
    public void Remove_SchedulesSave()
    {
        // Arrange
        var inner = new FakeUiStateService();
        inner.Set("key1", "value1");
        var debouncer = new FakeDebouncer();
        var decorator = new AutoSavingUiStateService(inner, debouncer);
        
        // Act
        decorator.Remove("key1");
        
        // Assert
        Assert.AreEqual(1, debouncer.TriggerCount, "Remove should schedule a save");
        Assert.IsNull(inner.Get<string>("key1"), "Remove should delete from inner state");
    }
    
    [TestMethod]
    public void Flush_ForcesImmediateSave()
    {
        // Arrange
        var inner = new FakeUiStateService();
        var debouncer = new FakeDebouncer();
        var decorator = new AutoSavingUiStateService(inner, debouncer);
        decorator.Set("key1", "value1");
        
        // Act
        decorator.Flush();
        
        // Assert
        Assert.AreEqual(1, inner.SaveCount, "Flush should invoke inner.Save() synchronously");
    }
    
    [TestMethod]
    public void Flush_WithNothingPending_IsNoOp()
    {
        // Arrange
        var inner = new FakeUiStateService();
        var debouncer = new FakeDebouncer();
        var decorator = new AutoSavingUiStateService(inner, debouncer);
        
        // Act
        decorator.Flush();
        
        // Assert: no exception, and save was called (safe to call anytime)
        Assert.AreEqual(1, inner.SaveCount);
    }
    
    [TestMethod]
    public void Get_PassesThroughToInner()
    {
        // Arrange
        var inner = new FakeUiStateService();
        inner.Set("key1", 42);
        var debouncer = new FakeDebouncer();
        var decorator = new AutoSavingUiStateService(inner, debouncer);
        
        // Act
        var value = decorator.Get<int>("key1");
        
        // Assert
        Assert.AreEqual(42, value);
    }
    
    [TestMethod]
    public void Save_PassesThroughToInner()
    {
        // Arrange
        var inner = new FakeUiStateService();
        var debouncer = new FakeDebouncer();
        var decorator = new AutoSavingUiStateService(inner, debouncer);
        
        // Act
        decorator.Save();
        
        // Assert
        Assert.AreEqual(1, inner.SaveCount);
    }
    
    [TestMethod]
    public void RemoveKeysNotIn_PassesThroughToInner()
    {
        // Arrange
        var inner = new FakeUiStateService();
        inner.Set("prefix.a", "1");
        inner.Set("prefix.b", "2");
        inner.Set("prefix.c", "3");
        var debouncer = new FakeDebouncer();
        var decorator = new AutoSavingUiStateService(inner, debouncer);
        
        // Act
        decorator.RemoveKeysNotIn("prefix.", new[] { "a", "c" });
        
        // Assert
        Assert.IsNotNull(inner.Get<string>("prefix.a"));
        Assert.IsNull(inner.Get<string>("prefix.b"), "Should be removed");
        Assert.IsNotNull(inner.Get<string>("prefix.c"));
    }

    // Test doubles
    private class FakeUiStateService : IUiStateService
    {
        private readonly Dictionary<string, object?> _data = new();
        public int SaveCount { get; private set; }
        
        public T? Get<T>(string key) =>
            _data.TryGetValue(key, out var val) ? (T?)val : default;
        
        public void Set<T>(string key, T value) => _data[key] = value;
        
        public void Remove(string key) => _data.Remove(key);
        
        public void Save() => SaveCount++;
        
        public void RemoveKeysNotIn(string keyPrefix, IReadOnlyCollection<string> liveSuffixes)
        {
            var live = new HashSet<string>(liveSuffixes);
            var toRemove = new List<string>();
            foreach (var key in _data.Keys)
            {
                if (!key.StartsWith(keyPrefix)) continue;
                var suffix = key.Substring(keyPrefix.Length);
                if (!live.Contains(suffix)) toRemove.Add(key);
            }
            foreach (var k in toRemove) _data.Remove(k);
        }
    }
    
    private class FakeDebouncer : IDebouncer
    {
        public int TriggerCount { get; private set; }
        public void Trigger() => TriggerCount++;
    }
}
