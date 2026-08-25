using Glasswork.Core.Models;

namespace Glasswork.Tests;

/// <summary>
/// Tests for <see cref="GlassworkTask.Clone"/> — the defensive-copy primitive the
/// in-memory Index uses to hand out task snapshots without aliasing its canonical
/// store (issue #184). Mutating a returned clone must never affect the original,
/// and the clone must reset transient UI fields (<see cref="GlassworkTask.IsManuallyCollapsed"/>,
/// <see cref="GlassworkTask.TodaysSubtasks"/>) that should not survive an index snapshot.
/// </summary>
[TestClass]
public class GlassworkTaskCloneTests
{
    [TestMethod]
    public void Clone_CopiesScalarFields()
    {
        var src = new GlassworkTask
        {
            Id = "t1",
            Title = "Original",
            Status = GlassworkTask.Statuses.InProgress,
            Priority = GlassworkTask.Priorities.High,
            Type = GlassworkTask.Types.Parent,
            SourceKind = "Feature",
            Size = "future_bucket",
            Created = new DateTime(2024, 1, 2),
            CompletedAt = new DateTime(2024, 5, 1),
            Due = new DateTime(2024, 6, 1),
            MyDay = new DateTime(2024, 5, 1),
            Parent = "parent-id",
            Description = "desc",
            Notes = "notes",
            IsV1Format = true,
        };

        var copy = src.Clone();

        Assert.AreEqual("t1", copy.Id);
        Assert.AreEqual("Original", copy.Title);
        Assert.AreEqual(GlassworkTask.Statuses.InProgress, copy.Status);
        Assert.AreEqual(GlassworkTask.Priorities.High, copy.Priority);
        Assert.AreEqual(GlassworkTask.Types.Parent, copy.Type);
        Assert.AreEqual("Feature", copy.SourceKind);
        Assert.AreEqual("future_bucket", copy.Size);
        Assert.AreEqual(new DateTime(2024, 1, 2), copy.Created);
        Assert.AreEqual(new DateTime(2024, 5, 1), copy.CompletedAt);
        Assert.AreEqual(new DateTime(2024, 6, 1), copy.Due);
        Assert.AreEqual(new DateTime(2024, 5, 1), copy.MyDay);
        Assert.AreEqual("parent-id", copy.Parent);
        Assert.AreEqual("desc", copy.Description);
        Assert.AreEqual("notes", copy.Notes);
        Assert.IsTrue(copy.IsV1Format);
    }

    [TestMethod]
    public void Clone_DeepCopiesSubtasksList_MutationIsolated()
    {
        var src = new GlassworkTask { Id = "t1", Title = "T" };
        src.Subtasks.Add(new SubTask { Text = "step 1", Status = "in_progress", Size = "future_bucket" });
        src.Subtasks[0].Metadata["blocker"] = "waiting on Alice";

        var copy = src.Clone();

        // Different list instance.
        Assert.AreNotSame(src.Subtasks, copy.Subtasks);
        Assert.AreEqual(1, copy.Subtasks.Count);
        // Different SubTask instance.
        Assert.AreNotSame(src.Subtasks[0], copy.Subtasks[0]);
        Assert.AreEqual("step 1", copy.Subtasks[0].Text);
        Assert.AreEqual("in_progress", copy.Subtasks[0].Status);
        Assert.AreEqual("future_bucket", copy.Subtasks[0].Size);
        // Different Metadata dict instance.
        Assert.AreNotSame(src.Subtasks[0].Metadata, copy.Subtasks[0].Metadata);
        Assert.AreEqual("waiting on Alice", copy.Subtasks[0].Metadata["blocker"]);

        // Mutating the clone leaves the source untouched.
        copy.Subtasks.Add(new SubTask { Text = "added" });
        copy.Subtasks[0].Text = "MUTATED";
        copy.Subtasks[0].Metadata["blocker"] = "MUTATED";
        Assert.AreEqual(1, src.Subtasks.Count);
        Assert.AreEqual("step 1", src.Subtasks[0].Text);
        Assert.AreEqual("waiting on Alice", src.Subtasks[0].Metadata["blocker"]);
    }

    [TestMethod]
    public void Clone_DeepCopiesLinks_MutationIsolated()
    {
        var src = new GlassworkTask { Id = "t1", Title = "T" };
        src.Links.Add(new TaskLink { Type = TaskLink.Types.Ado, Value = "1234", Label = "ADO #1234" });

        var copy = src.Clone();

        Assert.AreNotSame(src.Links, copy.Links);
        Assert.AreEqual(1, copy.Links.Count);
        Assert.AreEqual("1234", copy.Links[0].Value);

        copy.Links.Add(new TaskLink { Type = TaskLink.Types.Pr, Value = "https://pr" });
        Assert.AreEqual(1, src.Links.Count);
    }

    [TestMethod]
    public void Clone_DeepCopiesRelatedLinks_MutationIsolated()
    {
        var src = new GlassworkTask { Id = "t1", Title = "T" };
        src.RelatedLinks.Add(new RelatedLink { Slug = "decisions/foo", DisplayName = "Foo" });

        var copy = src.Clone();

        Assert.AreNotSame(src.RelatedLinks, copy.RelatedLinks);
        Assert.AreNotSame(src.RelatedLinks[0], copy.RelatedLinks[0]);
        Assert.AreEqual("decisions/foo", copy.RelatedLinks[0].Slug);

        copy.RelatedLinks[0].DisplayName = "MUTATED";
        Assert.AreEqual("Foo", src.RelatedLinks[0].DisplayName);
    }

    [TestMethod]
    public void Clone_DeepCopiesTagsAndContextLinks_MutationIsolated()
    {
        var src = new GlassworkTask { Id = "t1", Title = "T" };
        src.Tags.Add("eng");
        src.ContextLinks.Add("ctx-1");

        var copy = src.Clone();

        Assert.AreNotSame(src.Tags, copy.Tags);
        Assert.AreNotSame(src.ContextLinks, copy.ContextLinks);
        copy.Tags.Add("MUTATED");
        copy.ContextLinks.Add("MUTATED");
        Assert.AreEqual(1, src.Tags.Count);
        Assert.AreEqual(1, src.ContextLinks.Count);
    }

    [TestMethod]
    public void Clone_ResetsTransientUiFields()
    {
        var src = new GlassworkTask { Id = "t1", Title = "T", IsManuallyCollapsed = true };
        src.TodaysSubtasks = new[] { new SubTask { Text = "today" } };
        src.TodaysChildren = new[] { new GlassworkTask { Id = "child", Title = "Child" } };

        var copy = src.Clone();

        // The index should never expose UI-only collapse state or My-Day-virtual
        // promotion attachments — those are recomputed per page render.
        Assert.IsFalse(copy.IsManuallyCollapsed);
        Assert.IsNull(copy.TodaysSubtasks);
        Assert.IsNull(copy.TodaysChildren);
    }
}
