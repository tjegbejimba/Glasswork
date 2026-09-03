using Glasswork.Core.Models;

namespace Glasswork.Tests;

[TestClass]
public sealed class TaskDetailProjectionTests
{
    [TestMethod]
    public void Create_MinimalTask_UsesCanonicalDefaultsAndVisibility()
    {
        var projection = TaskDetailProjection.Create(new GlassworkTask
        {
            Id = "minimal",
            Title = "Minimal task",
            Status = "unknown",
            Priority = "medium",
            Type = "task",
            Due = new DateTime(2026, 9, 3),
            ResourceRevision = "rev-1",
        });

        Assert.AreEqual("minimal", projection.TaskId);
        Assert.AreEqual("Minimal task", projection.Title);
        Assert.AreEqual(GlassworkTask.Statuses.Todo, projection.Status.Value);
        Assert.AreEqual("To Do", projection.Status.Label);
        Assert.AreEqual("rev-1", projection.ResourceRevision);
        Assert.AreEqual(new DateTime(2026, 9, 3), projection.Due);
        Assert.IsFalse(projection.Visibility.IsReadOnly);
        Assert.IsFalse(projection.Visibility.ShowArtifacts);
        Assert.IsFalse(projection.Visibility.ShowRelated);
        Assert.IsFalse(projection.Visibility.ShowChildren);
        Assert.IsFalse(projection.Visibility.ShowBacklinks);
        Assert.IsTrue(projection.Visibility.ShowNotesEmptyHint);
    }

    [TestMethod]
    public void Create_RichTask_ProjectsProseMetadataAndRelationships()
    {
        var task = new GlassworkTask
        {
            Id = "rich",
            Title = "Rich task",
            Status = GlassworkTask.Statuses.InProgress,
            Priority = GlassworkTask.Priorities.High,
            Type = GlassworkTask.Types.Bug,
            Size = "medium",
            Description = "A description",
            Notes = "A note",
            Tags = ["one", "two"],
            ContextLinks = ["context"],
            BlockedBy = ["other-task"],
            Links =
            [
                new TaskLink { Type = TaskLink.Types.Doc, Value = "https://example.test/doc", Label = "Docs" },
            ],
            Subtasks =
            [
                new SubTask { Text = "Active", Status = "in_progress" },
                new SubTask { Text = "Finished", Status = "done" },
            ],
            ResourceRevision = "rev-rich",
        };

        var projection = TaskDetailProjection.Create(
            task,
            artifacts:
            [
                new Artifact("plan.md", "Plan", DateTime.UtcNow, "# plan"),
            ],
            children:
            [
                new GlassworkTask { Id = "child", Title = "Child" },
            ],
            backlinks:
            [
                new Backlink("wiki/decision.md", "Decision", BacklinkPageType.Decision, DateTime.UtcNow),
            ],
            relatedEntries:
            [
                new TaskDetailRelatedEntry("wiki/topic", null, "Topic", "note", null, false),
            ],
            nowUtc: DateTime.UtcNow);

        Assert.AreEqual("rev-rich", projection.ResourceRevision);
        Assert.AreEqual("medium", projection.Size);
        CollectionAssert.AreEqual(new[] { "one", "two" }, projection.Tags.ToArray());
        Assert.AreEqual("A description", projection.Description);
        Assert.AreEqual("A note", projection.Notes);
        Assert.AreEqual(1, projection.ActiveSubtasks.Count);
        Assert.AreEqual(1, projection.CompletedSubtasks.Count);
        Assert.AreEqual(1, projection.Artifacts.Count);
        Assert.AreEqual(1, projection.Links.Count);
        Assert.AreEqual(1, projection.RelatedEntries.Count);
        Assert.AreEqual(1, projection.DirectChildren.Count);
        Assert.AreEqual(1, projection.Backlinks.Count);
        Assert.IsTrue(projection.Visibility.ShowArtifacts);
        Assert.IsTrue(projection.Visibility.ShowRelated);
        Assert.IsTrue(projection.Visibility.ShowChildren);
        Assert.IsTrue(projection.Visibility.ShowBacklinks);
        Assert.IsTrue(projection.Visibility.ShowCompletedSubtasks);
    }

    [TestMethod]
    public void Create_BlockedAndCancelledTasks_ExposeLifecycleVisibility()
    {
        var blocked = TaskDetailProjection.Create(new GlassworkTask
        {
            Id = "blocked",
            Status = GlassworkTask.Statuses.Blocked,
            BlockedReason = "Waiting for approval",
            BlockedMetadataState = BlockedMetadataState.Valid,
        });
        var cancelled = TaskDetailProjection.Create(new GlassworkTask
        {
            Id = "cancelled",
            Status = GlassworkTask.Statuses.Cancelled,
            CancellationReason = "Superseded",
            CancelledAt = DateTimeOffset.UtcNow,
        });

        Assert.IsTrue(blocked.Status.IsBlocked);
        Assert.IsTrue(blocked.Visibility.ShowBlockedStatus);
        Assert.IsTrue(blocked.Visibility.ShowEditBlockerAction);
        Assert.IsTrue(blocked.Visibility.ShowResumeBlockedAction);
        Assert.IsTrue(blocked.Visibility.ShowMarkBlockedDoneAction);
        Assert.IsTrue(blocked.Visibility.ShowCancelAction);

        Assert.IsTrue(cancelled.Status.IsCancelled);
        Assert.IsTrue(cancelled.Status.IsTerminal);
        Assert.IsTrue(cancelled.Visibility.IsReadOnly);
        Assert.IsFalse(cancelled.Visibility.ShowCancelAction);
        Assert.IsTrue(cancelled.Visibility.ShowCancelledTimestamp);
    }

    [TestMethod]
    public void Create_MultiFormatArtifacts_PreservesDescriptorsAndMissingValues()
    {
        var now = new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc);
        var projection = TaskDetailProjection.Create(
            new GlassworkTask { Id = "formats" },
            artifacts:
            [
                new Artifact("readme.md", "Readme", now.AddMinutes(-4), "# readme") { Kind = ArtifactKind.Markdown },
                new Artifact("data.json", "Data", now.AddMinutes(-3), "{}") { Kind = ArtifactKind.Text },
                new Artifact("preview.html", "Preview", now.AddMinutes(-2), null) { Kind = ArtifactKind.Html },
                new Artifact("photo.png", "Photo", now.AddMinutes(-1), null)
                    { Kind = ArtifactKind.Image, SizeBytes = ArtifactCaps.InlineImageBytes + 1 },
                new Artifact("unknown.bin", "Unknown", now, null)
                    { Kind = ArtifactKind.Other, LoadError = "unsupported" },
            ],
            nowUtc: now);

        Assert.AreEqual(5, projection.Artifacts.Count);
        Assert.AreEqual(ArtifactKind.Markdown, projection.Artifacts[0].Kind);
        Assert.AreEqual(ArtifactKind.Text, projection.Artifacts[1].Kind);
        Assert.AreEqual(ArtifactKind.Html, projection.Artifacts[2].Kind);
        Assert.HasCount(5, projection.ArtifactRows);
        Assert.AreEqual("Unknown", projection.ArtifactRows[0].Title, "shared rows are newest-first");
        Assert.IsTrue(projection.ArtifactRows[0].HasLoadError);
        Assert.IsTrue(projection.ArtifactRows[0].IsReference);
        Assert.IsTrue(projection.ArtifactRows[1].IsReference);
        Assert.IsTrue(projection.ArtifactRows[0].IsExpanded, "newest bounded artifact auto-expands");
        Assert.IsFalse(projection.ArtifactRows[1].IsExpanded, "over-cap images never auto-expand");
        Assert.IsFalse(projection.ArtifactRows[2].IsExpanded, "only the newest artifact auto-expands");
    }

    [TestMethod]
    public void Create_MalformedOrMissingOptionalInputs_FailsClosedWithoutThrowing()
    {
        var projection = TaskDetailProjection.Create(
            new GlassworkTask
            {
                Id = "",
                Title = "",
                Status = null!,
                Priority = null!,
                Type = null!,
                Description = null!,
                Notes = null!,
                Subtasks = null!,
                Links = null!,
                Tags = null!,
                ContextLinks = null!,
                BlockedBy = null!,
                ResourceRevision = null,
            },
            artifacts: null,
            children: null,
            backlinks: null,
            relatedEntries: null);

        Assert.AreEqual(string.Empty, projection.TaskId);
        Assert.AreEqual(GlassworkTask.Statuses.Todo, projection.Status.Value);
        Assert.AreEqual(string.Empty, projection.Description);
        Assert.AreEqual(string.Empty, projection.Notes);
        Assert.AreEqual(string.Empty, projection.ResourceRevision);
        Assert.AreEqual(0, projection.ActiveSubtasks.Count);
        Assert.AreEqual(0, projection.Links.Count);
    }

    [TestMethod]
    public void Service_BuildMissingTask_ReturnsNull()
    {
        var vaultPath = Path.Combine(Path.GetTempPath(), "glasswork-detail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultPath);
        try
        {
            var service = new Glasswork.Core.Services.TaskDetailProjectionService(
                new Glasswork.Core.Services.VaultService(vaultPath));

            Assert.IsNull(service.Build("does-not-exist"));
            Assert.IsNull(service.Build(string.Empty));
        }
        finally
        {
            if (Directory.Exists(vaultPath))
                Directory.Delete(vaultPath, recursive: true);
        }
    }

    [TestMethod]
    public void Service_BuildMalformedRelatedLink_PreservesOtherEntries()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-related-" + Guid.NewGuid().ToString("N"));
        var todoPath = Path.Combine(vaultRoot, "wiki", "todo");
        var conceptsPath = Path.Combine(vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(todoPath);
        Directory.CreateDirectory(conceptsPath);
        try
        {
            File.WriteAllText(
                Path.Combine(conceptsPath, "valid.md"),
                "---\ntitle: Valid page\ntype: concept\n---\n");

            var vault = new Glasswork.Core.Services.VaultService(todoPath);
            var task = new GlassworkTask
            {
                Id = "related-task",
                Title = "Related task",
                RelatedLinks =
                [
                    new RelatedLink { Slug = "concepts/valid" },
                    new RelatedLink { Slug = "bad\0slug" },
                ],
            };
            vault.Save(task);

            var projection = new Glasswork.Core.Services.TaskDetailProjectionService(vault).Build(task);

            Assert.IsNotNull(projection);
            Assert.AreEqual(2, projection.RelatedEntries.Count);
            Assert.AreEqual("Valid page", projection.RelatedEntries[0].Title);
            Assert.AreEqual("bad\0slug", projection.RelatedEntries[1].Slug);
        }
        finally
        {
            if (Directory.Exists(vaultRoot))
                Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [TestMethod]
    public void Service_BuildSeededVaultFixtures_PreservesLifecycleAndArtifactKinds()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "glasswork-fixtures-" + Guid.NewGuid().ToString("N"));
        var todoPath = Path.Combine(vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(todoPath);
        try
        {
            var vault = new Glasswork.Core.Services.VaultService(todoPath);
            vault.Save(new GlassworkTask { Id = "fixture-minimal", Title = "Minimal", Status = "todo" });
            vault.Save(new GlassworkTask
            {
                Id = "fixture-blocked",
                Title = "Blocked",
                Status = GlassworkTask.Statuses.Blocked,
                BlockedReason = "Waiting",
                BlockedMetadataState = BlockedMetadataState.Valid,
            });
            vault.Save(new GlassworkTask
            {
                Id = "fixture-cancelled",
                Title = "Cancelled",
                Status = GlassworkTask.Statuses.Cancelled,
                CancellationReason = "Superseded",
                CancelledAt = DateTimeOffset.UtcNow,
            });

            var artifactPath = Path.Combine(todoPath, "fixture-artifacts.artifacts");
            Directory.CreateDirectory(artifactPath);
            File.WriteAllText(Path.Combine(artifactPath, "readme.md"), "# readme");
            File.WriteAllText(Path.Combine(artifactPath, "preview.html"), "<p>preview</p>");
            File.WriteAllText(Path.Combine(artifactPath, "data.txt"), "data");
            File.WriteAllBytes(Path.Combine(artifactPath, "unknown.bin"), [0, 1, 2]);
            vault.Save(new GlassworkTask { Id = "fixture-artifacts", Title = "Artifacts", Status = "todo" });

            var service = new Glasswork.Core.Services.TaskDetailProjectionService(
                vault,
                new Glasswork.Core.Services.FileSystemArtifactStore(vaultRoot));

            var minimal = service.Build("fixture-minimal");
            var blocked = service.Build("fixture-blocked");
            var cancelled = service.Build("fixture-cancelled");
            var artifacts = service.Build("fixture-artifacts");

            Assert.IsNotNull(minimal);
            Assert.IsNotNull(blocked);
            Assert.IsNotNull(cancelled);
            Assert.IsNotNull(artifacts);
            Assert.AreEqual(GlassworkTask.Statuses.Todo, minimal.Status.Value);
            Assert.IsTrue(blocked.Status.IsBlocked);
            Assert.IsTrue(cancelled.Status.IsCancelled);
            Assert.AreEqual(4, artifacts.Artifacts.Count);
            CollectionAssert.AreEquivalent(
                new[] { ArtifactKind.Markdown, ArtifactKind.Html, ArtifactKind.Text, ArtifactKind.Other },
                artifacts.Artifacts.Select(a => a.Kind).ToArray());
        }
        finally
        {
            if (Directory.Exists(vaultRoot))
                Directory.Delete(vaultRoot, recursive: true);
        }
    }
}
