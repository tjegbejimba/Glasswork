using Glasswork.Core.Services;

namespace Glasswork.Tests;

/// <summary>
/// Unit coverage for <see cref="SessionTaskSetStateStore"/>: round-tripping,
/// per-session isolation, cross-process merge safety, and visible failure on
/// malformed or future-version persisted state. See issue #557 and ADR 0026.
/// </summary>
[TestClass]
public class SessionTaskSetStateStoreTests
{
    private static string NewStatePath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glasswork-session-task-set-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ui-state.json");
    }

    [TestMethod]
    public void Load_ReturnsSuccessfulEmpty_WhenSessionWasNeverSaved()
    {
        var ui = new JsonFileUiStateService(NewStatePath());
        var store = new SessionTaskSetStateStore(ui);

        var result = store.Load("session-never-saved");

        Assert.IsTrue(result.Ok, "a session with no persisted key is a legitimate empty Session Task Set");
        Assert.AreEqual(0, result.Members.Count);
    }

    [TestMethod]
    public void Save_ThenLoad_RoundTripsOrderedMembersAndTitles()
    {
        var ui = new JsonFileUiStateService(NewStatePath());
        var store = new SessionTaskSetStateStore(ui);
        var members = new[]
        {
            new SessionTaskSetMemberState("third", "Third task"),
            new SessionTaskSetMemberState("second", "Second task"),
            new SessionTaskSetMemberState("demo", "Demo task"),
        };

        store.Save("session-round-trip", members);
        var result = store.Load("session-round-trip");

        Assert.IsTrue(result.Ok);
        CollectionAssert.AreEqual(members, result.Members.ToArray(), "order and titles must round-trip exactly");
    }

    [TestMethod]
    public void Save_ThenLoad_PersistsAcrossInstances()
    {
        var path = NewStatePath();
        var writer = new SessionTaskSetStateStore(new JsonFileUiStateService(path));
        writer.Save("session-cold", [new SessionTaskSetMemberState("demo", "Demo task")]);

        // A brand-new IUiStateService instance (simulates a restarted host process).
        var reader = new SessionTaskSetStateStore(new JsonFileUiStateService(path));
        var result = reader.Load("session-cold");

        Assert.IsTrue(result.Ok);
        Assert.AreEqual(1, result.Members.Count);
        Assert.AreEqual("demo", result.Members[0].TaskId);
        Assert.AreEqual("Demo task", result.Members[0].Title);
    }

    [TestMethod]
    public void Load_IsIsolatedPerSessionId()
    {
        var ui = new JsonFileUiStateService(NewStatePath());
        var store = new SessionTaskSetStateStore(ui);
        store.Save("session-a", [new SessionTaskSetMemberState("task-a", "Task A")]);
        store.Save("session-b", [new SessionTaskSetMemberState("task-b", "Task B")]);

        var a = store.Load("session-a");
        var b = store.Load("session-b");

        Assert.AreEqual(1, a.Members.Count);
        Assert.AreEqual("task-a", a.Members[0].TaskId);
        Assert.AreEqual(1, b.Members.Count);
        Assert.AreEqual("task-b", b.Members[0].TaskId);
    }

    [TestMethod]
    public void Save_WithEmptyMembers_RemovesThePersistedKeyEntirely()
    {
        var ui = new JsonFileUiStateService(NewStatePath());
        var store = new SessionTaskSetStateStore(ui);
        store.Save("session-clear", [new SessionTaskSetMemberState("demo", "Demo task")]);

        store.Save("session-clear", []);
        var result = store.Load("session-clear");

        Assert.IsTrue(result.Ok, "clearing must never look like a malformed read");
        Assert.AreEqual(0, result.Members.Count);
        Assert.IsNull(ui.Get<object>(SessionTaskSetStateStore.KeyPrefix + "session-clear"), "the key must be removed, not just emptied");
    }

    [TestMethod]
    public void Load_FailsVisibly_WhenPersistedVersionIsUnrecognized()
    {
        var ui = new JsonFileUiStateService(NewStatePath());
        ui.Set(SessionTaskSetStateStore.KeyPrefix + "session-future", new { version = 99, members = new[] { new { taskId = "demo", title = "Demo" } } });
        var store = new SessionTaskSetStateStore(ui);

        var result = store.Load("session-future");

        Assert.IsFalse(result.Ok, "an unrecognized version must fail visibly, not silently become an empty set");
        Assert.AreEqual("unsupported_version", result.ErrorCode);
        Assert.AreEqual(0, result.Members.Count);
    }

    [TestMethod]
    public void Load_FailsVisibly_WhenPersistedShapeIsMalformed()
    {
        var ui = new JsonFileUiStateService(NewStatePath());
        ui.Set(SessionTaskSetStateStore.KeyPrefix + "session-malformed", "not an object");
        var store = new SessionTaskSetStateStore(ui);

        var result = store.Load("session-malformed");

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("malformed_state", result.ErrorCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [TestMethod]
    public void Load_FailsVisibly_WhenAMemberIsMissingATaskId()
    {
        var ui = new JsonFileUiStateService(NewStatePath());
        ui.Set(SessionTaskSetStateStore.KeyPrefix + "session-bad-member", new { version = 1, members = new[] { new { title = "No id" } } });
        var store = new SessionTaskSetStateStore(ui);

        var result = store.Load("session-bad-member");

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("malformed_state", result.ErrorCode);
    }

    [TestMethod]
    public void Save_MergesAcrossConcurrentInstances_WithoutClobberingOtherSessions()
    {
        var path = NewStatePath();
        var hostA = new SessionTaskSetStateStore(new JsonFileUiStateService(path));
        var hostB = new SessionTaskSetStateStore(new JsonFileUiStateService(path));

        // Both processes start from the same (empty) disk state, then save
        // their own distinct session keys — mirrors two concurrent canvas
        // hosts backing different Copilot sessions against one Vault.
        hostA.Save("session-a", [new SessionTaskSetMemberState("task-a", "Task A")]);
        hostB.Save("session-b", [new SessionTaskSetMemberState("task-b", "Task B")]);

        var verifier = new SessionTaskSetStateStore(new JsonFileUiStateService(path));
        var a = verifier.Load("session-a");
        var b = verifier.Load("session-b");

        Assert.IsTrue(a.Ok);
        Assert.AreEqual(1, a.Members.Count, "session-a's save must survive session-b's concurrent save");
        Assert.IsTrue(b.Ok);
        Assert.AreEqual(1, b.Members.Count);
    }
}
