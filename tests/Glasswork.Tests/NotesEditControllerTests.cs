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
}
