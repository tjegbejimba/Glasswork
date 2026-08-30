using Glasswork.Core.Models;
using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public sealed class ChildActivitySummaryServiceTests
{
    private string _taskDirectory = null!;
    private VaultService _vault = null!;
    private ChildActivitySummaryService _service = null!;
    private SelfWriteCoordinator _selfWrites = null!;

    [TestInitialize]
    public void Initialize()
    {
        _taskDirectory = Path.Combine(
            Path.GetTempPath(),
            "glasswork-child-summary-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_taskDirectory);
        _selfWrites = new SelfWriteCoordinator(_taskDirectory);
        _vault = new VaultService(_taskDirectory, _selfWrites);
        _service = new ChildActivitySummaryService(
            _taskDirectory,
            _vault,
            new ResourceMutationService(_taskDirectory, _vault));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_taskDirectory))
            Directory.Delete(_taskDirectory, recursive: true);
    }

    [TestMethod]
    public void Capture_GroupsTheFullDescendantTreeByDirectChildInTitleOrder()
    {
        Save(Parent("root", "Root"));
        Save(Parent("zeta", "Zeta", "root", notes: "Zeta notes"));
        Save(Parent("alpha", "Alpha", "root", notes: "Alpha notes"));
        Save(Leaf("grandchild", "Grandchild", "alpha", notes: "Durable note"));

        var capture = _service.Capture("root");

        Assert.AreEqual(3, capture.DescendantCount);
        CollectionAssert.AreEqual(
            new[] { "alpha", "zeta" },
            capture.Groups.Select(group => group.DirectChild.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "alpha", "grandchild" },
            capture.Groups[0].Tasks.Select(task => task.Id).ToArray());
        Assert.AreEqual("Durable note", capture.Groups[0].Tasks[1].Notes);
        Assert.IsFalse(
            capture.Groups[0].Tasks[1].GetType().GetProperties()
                .Any(property => property.Name == nameof(GlassworkTask.Description)));
    }

    [TestMethod]
    public void Capture_IncludesOnlyDurableSummaryInputsAndTracksArtifactRevisions()
    {
        Save(Parent("root", "Root"));
        var child = Leaf("child", "Child", "root", notes: "Durable note");
        child.Links =
        [
            new TaskLink
            {
                Type = TaskLink.Types.Pr,
                Value = "https://github.com/example/repo/pull/7",
                Label = "Delivery",
            },
        ];
        Save(child);
        WriteArtifact("child", "plan.md", "# Plan");
        WriteArtifact("child", ChildActivitySummaryService.Filename, "# Nested summary");

        var capture = _service.Capture("root");
        var input = capture.Groups.Single().DirectChild;

        Assert.AreEqual("Durable note", input.Notes);
        Assert.AreEqual(TaskLink.Types.Pr, input.Links.Single().Type);
        CollectionAssert.AreEqual(
            new[] { ChildActivitySummaryService.Filename, "plan.md" },
            input.Artifacts.Select(artifact => artifact.Filename).ToArray());
        Assert.IsTrue(input.Artifacts[0].IsDescendantSummary);
        Assert.IsTrue(capture.ReadBasis.ContainsKey("task:child"));
        Assert.IsTrue(capture.ReadBasis.ContainsKey("artifact:child/plan.md"));
        Assert.IsTrue(capture.ReadBasis.ContainsKey(
            $"artifact:child/{ChildActivitySummaryService.Filename}"));
    }

    [TestMethod]
    public void Commit_CreatesStableArtifactWithMetadataAndCurrentReadState()
    {
        Save(Parent("root", "Root"));
        Save(Leaf("child", "Child", "root"));
        var generatedAt = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var capture = _service.Capture("root");

        var outcome = _service.Commit(
            capture,
            "## Alpha\n\nChild work is progressing.",
            generatedAt,
            "summary-create");
        var state = _service.ReadState("root");

        Assert.AreEqual("applied", outcome.Outcome);
        Assert.AreEqual(ChildActivitySummaryStateKind.Current, state.Kind);
        Assert.AreEqual(generatedAt, state.GeneratedAt);
        Assert.AreEqual(1, state.DescendantCount);
        StringAssert.Contains(state.Body, "Child work is progressing.");
        var stored = File.ReadAllText(SummaryPath("root"));
        StringAssert.Contains(stored, "generated_at: 2026-08-30T12:00:00.0000000+00:00");
        StringAssert.Contains(stored, "descendant_count: 1");
        StringAssert.Contains(stored, "read_basis_json:");
    }

    [TestMethod]
    public void ReadState_BecomesOutOfDateWhenTaskOrArtifactInputChanges()
    {
        Save(Parent("root", "Root"));
        Save(Leaf("child", "Child", "root"));
        WriteArtifact("child", "plan.md", "v1");
        var first = _service.Capture("root");
        _service.Commit(first, "Initial", DateTimeOffset.UtcNow, "summary-initial");
        Assert.AreEqual(ChildActivitySummaryStateKind.Current, _service.ReadState("root").Kind);

        var child = _vault.Load("child")!;
        child.Notes = "changed";
        _vault.Save(child);
        Assert.AreEqual(ChildActivitySummaryStateKind.OutOfDate, _service.ReadState("root").Kind);

        var refreshed = _service.Capture("root");
        _service.Commit(refreshed, "After task edit", DateTimeOffset.UtcNow, "summary-task-refresh");
        WriteArtifact("child", "plan.md", "v2");

        Assert.AreEqual(ChildActivitySummaryStateKind.OutOfDate, _service.ReadState("root").Kind);
    }

    [TestMethod]
    public void Commit_RejectsMissingDescendantAndPreservesPreviousSummary()
    {
        Save(Parent("root", "Root"));
        Save(Leaf("child", "Child", "root"));
        var initial = _service.Capture("root");
        _service.Commit(initial, "Previous summary", DateTimeOffset.UtcNow, "summary-previous");
        var staleCapture = _service.Capture("root");
        File.Delete(Path.Combine(_taskDirectory, "child.md"));

        var outcome = _service.Commit(
            staleCapture,
            "Must not replace",
            DateTimeOffset.UtcNow,
            "summary-missing-child");

        Assert.AreEqual("conflict", outcome.Outcome);
        StringAssert.Contains(outcome.Error, "Task 'child' is missing");
        StringAssert.Contains(File.ReadAllText(SummaryPath("root")), "Previous summary");
        Assert.IsFalse(Directory.EnumerateFiles(
            Path.GetDirectoryName(SummaryPath("root"))!,
            "*.tmp*").Any());
    }

    [TestMethod]
    public void Commit_RejectsConcurrentSummaryReplacement()
    {
        Save(Parent("root", "Root"));
        var capture = _service.Capture("root");
        WriteArtifact("root", ChildActivitySummaryService.Filename, "Concurrent writer");

        var outcome = _service.Commit(
            capture,
            "Generated from stale artifact basis",
            DateTimeOffset.UtcNow,
            "summary-conflict");

        Assert.AreEqual("conflict", outcome.Outcome);
        StringAssert.Contains(outcome.Error, "Artifact already exists");
        Assert.AreEqual("Concurrent writer", File.ReadAllText(SummaryPath("root")));
    }

    [TestMethod]
    public void CaptureAndCommit_RejectNonParentTasks()
    {
        Save(new GlassworkTask { Id = "leaf", Title = "Leaf" });

        var exception = Assert.ThrowsExactly<ChildActivitySummaryException>(
            () => _service.Capture("leaf"));

        Assert.AreEqual("not_parent", exception.Code);
    }

    [TestMethod]
    public void ReadState_ReportsMissingAndMalformedSummaryPrecisely()
    {
        Save(Parent("root", "Root"));
        Assert.AreEqual(ChildActivitySummaryStateKind.Missing, _service.ReadState("root").Kind);

        WriteArtifact("root", ChildActivitySummaryService.Filename, "# Not managed metadata");
        var malformed = _service.ReadState("root");

        Assert.AreEqual(ChildActivitySummaryStateKind.Failed, malformed.Kind);
        StringAssert.Contains(malformed.Error, "metadata");
    }

    [TestMethod]
    public void Commit_ReplacesTheSingleStableArtifactAndRegistersSelfWrite()
    {
        Save(Parent("root", "Root"));
        var first = _service.Capture("root");
        _service.Commit(first, "Version one", DateTimeOffset.UtcNow, "summary-v1");
        var second = _service.Capture("root");

        var outcome = _service.Commit(
            second,
            "Version two",
            DateTimeOffset.UtcNow,
            "summary-v2");

        Assert.AreEqual("applied", outcome.Outcome);
        StringAssert.Contains(File.ReadAllText(SummaryPath("root")), "Version two");
        Assert.HasCount(1, Directory.GetFiles(
            Path.GetDirectoryName(SummaryPath("root"))!,
            ChildActivitySummaryService.Filename));
        Assert.IsTrue(_selfWrites.IsOwnProcessWrite(SummaryPath("root")));
        Assert.IsFalse(Directory.EnumerateFiles(
            Path.GetDirectoryName(SummaryPath("root"))!,
            "*.tmp*").Any());
    }

    private void Save(GlassworkTask task) => _vault.Save(task, ifAbsent: true);

    private void WriteArtifact(string taskId, string filename, string content)
    {
        var folder = Path.Combine(_taskDirectory, taskId + ".artifacts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, filename), content);
    }

    private string SummaryPath(string taskId) =>
        Path.Combine(
            _taskDirectory,
            taskId + ".artifacts",
            ChildActivitySummaryService.Filename);

    private static GlassworkTask Parent(
        string id,
        string title,
        string? parent = null,
        string notes = "") =>
        new()
        {
            Id = id,
            Title = title,
            Type = GlassworkTask.Types.Parent,
            Parent = parent,
            Notes = notes,
        };

    private static GlassworkTask Leaf(
        string id,
        string title,
        string parent,
        string notes = "") =>
        new()
        {
            Id = id,
            Title = title,
            Parent = parent,
            Notes = notes,
            Description = "This must not enter summary input.",
        };
}
