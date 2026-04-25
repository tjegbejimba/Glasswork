using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class NotesEditControllerTests
{
    [TestMethod]
    public void DefaultsToReadMode_WithGivenBaseline()
    {
        var c = new NotesEditController("hello");
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
        Assert.AreEqual("hello", c.Baseline);
    }

    [TestMethod]
    public void EnterEdit_TransitionsToEdit_AndSeedsBufferFromBaseline()
    {
        var c = new NotesEditController("hello");
        c.EnterEdit();
        Assert.AreEqual(NotesEditMode.Edit, c.Mode);
        Assert.AreEqual("hello", c.Buffer);
    }

    [TestMethod]
    public void Done_PromotesBufferToBaseline_AndReturnsToRead()
    {
        var c = new NotesEditController("old");
        c.EnterEdit();
        c.UpdateBuffer("new content");
        var saved = c.Done();

        Assert.AreEqual("new content", saved);
        Assert.AreEqual("new content", c.Baseline);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
    }

    [TestMethod]
    public void Cancel_DiscardsBuffer_RestoresBaseline_AndReturnsToRead()
    {
        var c = new NotesEditController("old");
        c.EnterEdit();
        c.UpdateBuffer("xyz");
        var restored = c.Cancel();

        Assert.AreEqual("old", restored);
        Assert.AreEqual("old", c.Baseline);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);

        c.EnterEdit();
        Assert.AreEqual("old", c.Buffer, "Re-entering edit after cancel must seed from baseline, not the discarded buffer.");
    }

    [TestMethod]
    public void EnterEdit_IsNoOp_WhenAlreadyInEdit_AndDoesNotResetBuffer()
    {
        var c = new NotesEditController("old");
        c.EnterEdit();
        c.UpdateBuffer("typing...");
        c.EnterEdit();
        Assert.AreEqual(NotesEditMode.Edit, c.Mode);
        Assert.AreEqual("typing...", c.Buffer);
    }

    [TestMethod]
    public void Done_IsNoOp_WhenInReadMode()
    {
        var c = new NotesEditController("baseline");
        var result = c.Done();
        Assert.AreEqual("baseline", result);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
    }

    [TestMethod]
    public void Cancel_IsNoOp_WhenInReadMode()
    {
        var c = new NotesEditController("baseline");
        var result = c.Cancel();
        Assert.AreEqual("baseline", result);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
    }

    [TestMethod]
    public void OnExternalSave_InReadMode_UpdatesBaseline()
    {
        var c = new NotesEditController("v1");
        c.OnExternalSave("v2");
        Assert.AreEqual("v2", c.Baseline);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
    }

    [TestMethod]
    public void OnExternalSave_InEditMode_UpdatesBaseline_ButDoesNotClobberBuffer()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        c.UpdateBuffer("user typed this");
        c.OnExternalSave("v2-from-disk");

        Assert.AreEqual("v2-from-disk", c.Baseline);
        Assert.AreEqual("user typed this", c.Buffer, "External save must not destroy in-flight user edits.");
        Assert.AreEqual(NotesEditMode.Edit, c.Mode);
    }

    [TestMethod]
    public void ModeChanged_FiresOnEnterEdit_Done_AndCancel_ButNotOnNoOps()
    {
        var c = new NotesEditController("x");
        var transitions = new List<NotesEditMode>();
        c.ModeChanged += (_, m) => transitions.Add(m);

        c.EnterEdit();
        c.EnterEdit();
        c.Done();
        c.Done();
        c.EnterEdit();
        c.Cancel();
        c.Cancel();

        CollectionAssert.AreEqual(
            new[] { NotesEditMode.Edit, NotesEditMode.Read, NotesEditMode.Edit, NotesEditMode.Read },
            transitions);
    }

    [TestMethod]
    public void Constructor_NullBaseline_TreatedAsEmptyString()
    {
        var c = new NotesEditController(null);
        Assert.AreEqual(string.Empty, c.Baseline);
        c.EnterEdit();
        Assert.AreEqual(string.Empty, c.Buffer);
    }

    [TestMethod]
    public void UpdateBuffer_NullCoercedToEmptyString()
    {
        var c = new NotesEditController("x");
        c.EnterEdit();
        c.UpdateBuffer(null);
        Assert.AreEqual(string.Empty, c.Buffer);
    }

    // ─── M8: External-change classification (close agent-write loss) ─────────

    [TestMethod]
    public void Classify_ReadMode_DiskUnchanged_Ignore()
    {
        var c = new NotesEditController("v1");
        Assert.AreEqual(NotesExternalChangeAction.Ignore, c.ClassifyExternalChange("v1"));
    }

    [TestMethod]
    public void Classify_ReadMode_DiskChanged_SilentRefresh()
    {
        var c = new NotesEditController("v1");
        Assert.AreEqual(NotesExternalChangeAction.SilentRefresh, c.ClassifyExternalChange("v2"));
    }

    [TestMethod]
    public void Classify_EditMode_NoTyping_DiskChanged_SilentRefresh()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        // buffer == baseline (user hasn't typed anything yet)
        Assert.AreEqual(NotesExternalChangeAction.SilentRefresh, c.ClassifyExternalChange("v2"));
    }

    [TestMethod]
    public void Classify_EditMode_UserTyped_DiskChanged_Conflict()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        c.UpdateBuffer("user mid-sentence");
        Assert.AreEqual(NotesExternalChangeAction.Conflict, c.ClassifyExternalChange("v2-from-agent"));
    }

    [TestMethod]
    public void Classify_EditMode_UserTyped_DiskUnchanged_Ignore()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        c.UpdateBuffer("user typed");
        Assert.AreEqual(NotesExternalChangeAction.Ignore, c.ClassifyExternalChange("v1"));
    }

    [TestMethod]
    public void Classify_NullDiskValue_TreatedAsEmpty()
    {
        var c = new NotesEditController("v1");
        Assert.AreEqual(NotesExternalChangeAction.SilentRefresh, c.ClassifyExternalChange(null));
        var c2 = new NotesEditController(string.Empty);
        Assert.AreEqual(NotesExternalChangeAction.Ignore, c2.ClassifyExternalChange(null));
    }

    [TestMethod]
    public void ApplySilentRefresh_ReadMode_UpdatesBaseline()
    {
        var c = new NotesEditController("v1");
        c.ApplySilentRefresh("v2");
        Assert.AreEqual("v2", c.Baseline);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
    }

    [TestMethod]
    public void ApplySilentRefresh_EditMode_NoTyping_UpdatesBufferAndBaseline_StaysInEdit()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        c.ApplySilentRefresh("v2-from-agent");

        Assert.AreEqual("v2-from-agent", c.Baseline);
        Assert.AreEqual("v2-from-agent", c.Buffer, "Pristine buffer must follow disk so the user sees the agent's changes.");
        Assert.AreEqual(NotesEditMode.Edit, c.Mode);
    }

    [TestMethod]
    public void ApplyDiscardAndReload_ReplacesBufferWithDisk_TransitionsToRead()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        c.UpdateBuffer("user typed lots");
        var transitions = new List<NotesEditMode>();
        c.ModeChanged += (_, m) => transitions.Add(m);

        var result = c.ApplyDiscardAndReload("v2-from-agent");

        Assert.AreEqual("v2-from-agent", result);
        Assert.AreEqual("v2-from-agent", c.Baseline);
        Assert.AreEqual("v2-from-agent", c.Buffer);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
        CollectionAssert.AreEqual(new[] { NotesEditMode.Read }, transitions);
    }

    [TestMethod]
    public void ApplyKeepAndOverwrite_UpdatesBaselineOnly_BufferAndModeUntouched()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        c.UpdateBuffer("user mid-sentence");
        var transitions = new List<NotesEditMode>();
        c.ModeChanged += (_, m) => transitions.Add(m);

        c.ApplyKeepAndOverwrite("v2-from-agent");

        Assert.AreEqual("v2-from-agent", c.Baseline,
            "After 'keep mine', baseline must snap to the new disk content so the next external change doesn't re-trigger until it actually changes again.");
        Assert.AreEqual("user mid-sentence", c.Buffer);
        Assert.AreEqual(NotesEditMode.Edit, c.Mode);
        Assert.AreEqual(0, transitions.Count, "Mode must not transition.");
    }

    [TestMethod]
    public void ApplyKeepAndOverwrite_NextClassifyOnSameDisk_ReturnsIgnore()
    {
        var c = new NotesEditController("v1");
        c.EnterEdit();
        c.UpdateBuffer("user typed");
        c.ApplyKeepAndOverwrite("v2-from-agent");

        Assert.AreEqual(NotesExternalChangeAction.Ignore, c.ClassifyExternalChange("v2-from-agent"),
            "Once snapped to v2, a subsequent watcher tick reading the same v2 must not re-fire the conflict.");
    }

    // ─── Tracer-bullet scenarios from the issue ──────────────────────────────

    [TestMethod]
    public void Scenario1_ReadMode_AgentAppendsLine_SilentRefresh()
    {
        var c = new NotesEditController("first line");
        var classification = c.ClassifyExternalChange("first line\nsecond line");
        Assert.AreEqual(NotesExternalChangeAction.SilentRefresh, classification);

        c.ApplySilentRefresh("first line\nsecond line");
        Assert.AreEqual("first line\nsecond line", c.Baseline);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
    }

    [TestMethod]
    public void Scenario2_EditMode_NoTyping_AgentAppendsLine_BufferUpdatesSilently()
    {
        var c = new NotesEditController("first line");
        c.EnterEdit();
        // user hasn't typed
        var classification = c.ClassifyExternalChange("first line\nsecond line");
        Assert.AreEqual(NotesExternalChangeAction.SilentRefresh, classification);

        c.ApplySilentRefresh("first line\nsecond line");
        Assert.AreEqual("first line\nsecond line", c.Buffer);
        Assert.AreEqual("first line\nsecond line", c.Baseline);
        Assert.AreEqual(NotesEditMode.Edit, c.Mode);
    }

    [TestMethod]
    public void Scenario3_EditMode_UserTyped_AgentRewrites_ShowsConflict()
    {
        var c = new NotesEditController("first line");
        c.EnterEdit();
        c.UpdateBuffer("first line and my edits");

        var classification = c.ClassifyExternalChange("first line\nagent appended");
        Assert.AreEqual(NotesExternalChangeAction.Conflict, classification);

        // Discard mine and reload → user sees disk
        c.ApplyDiscardAndReload("first line\nagent appended");
        Assert.AreEqual("first line\nagent appended", c.Buffer);
        Assert.AreEqual(NotesEditMode.Read, c.Mode);
    }
}
