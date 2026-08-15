using Glasswork.Core.Models;
using Glasswork.Core.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Glasswork.Tests;

[TestClass]
public sealed class ResourceMutationDeletionTests
{
    private string _vaultRoot = null!;
    private string _todoPath = null!;
    private VaultService _vault = null!;
    private ResourceMutationService _mutations = null!;

    [TestInitialize]
    public void Setup()
    {
        _vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-delete-tests",
            Guid.NewGuid().ToString("N"));
        _todoPath = Path.Combine(_vaultRoot, "wiki", "todo");
        Directory.CreateDirectory(_todoPath);
        var selfWrites = new SelfWriteCoordinator(_todoPath);
        _vault = new VaultService(_todoPath, selfWrites);
        _mutations = new ResourceMutationService(_todoPath, _vault);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_vaultRoot))
            Directory.Delete(_vaultRoot, recursive: true);
    }

    [TestMethod]
    public void PreflightTaskDeletion_DescribesTheTaskWithoutMutatingTheVault()
    {
        _vault.Save(new GlassworkTask { Id = "obsolete-plan", Title = "Obsolete plan" });

        var result = _mutations.PreflightTaskDeletion("obsolete-plan");

        Assert.AreEqual("ready", result.Outcome);
        Assert.IsNotNull(result.Preflight);
        Assert.AreEqual("obsolete-plan", result.Preflight.Task.Id);
        Assert.AreEqual("Obsolete plan", result.Preflight.Task.Title);
        StringAssert.StartsWith(result.Preflight.Task.ResourceRevision, "rr1-");
        Assert.IsEmpty(result.Preflight.Descendants);
        Assert.IsEmpty(result.Preflight.Artifacts);
        Assert.IsEmpty(result.Preflight.BacklinkPages);
        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "obsolete-plan.md")));
    }

    [TestMethod]
    public void PreflightTaskDeletion_ReturnsTheCompleteDescendantSubtree()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        _vault.Save(new GlassworkTask { Id = "child-b", Title = "Child B", Parent = "root" });
        _vault.Save(new GlassworkTask { Id = "child-a", Title = "Child A", Parent = " root " });
        _vault.Save(new GlassworkTask { Id = "grandchild", Title = "Grandchild", Parent = "child-a" });
        _vault.Save(new GlassworkTask { Id = "unrelated", Title = "Unrelated" });

        var result = _mutations.PreflightTaskDeletion("root");

        Assert.AreEqual("ready", result.Outcome);
        CollectionAssert.AreEqual(
            new[] { "child-a", "child-b", "grandchild" },
            result.Preflight!.Descendants.Select(task => task.Id).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "child-a.md")));
        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "grandchild.md")));
    }

    [TestMethod]
    public void PreflightTaskDeletion_ResolvesPbiChildrenByAdoParentIdentity()
    {
        _vault.Save(new GlassworkTask
        {
            Id = "parent-pbi",
            Title = "Parent PBI",
            Type = GlassworkTask.Types.Pbi,
            Links =
            [
                new TaskLink
                {
                    Type = TaskLink.Types.Ado,
                    Value = "42",
                },
            ],
        });
        _vault.Save(new GlassworkTask
        {
            Id = "ado-child",
            Title = "ADO child",
            Parent = "https://dev.azure.com/org/project/_workitems/edit/42",
        });
        _vault.Save(new GlassworkTask
        {
            Id = "ado-grandchild",
            Title = "ADO grandchild",
            Parent = "ado-child",
        });

        var result = _mutations.PreflightTaskDeletion("parent-pbi");

        CollectionAssert.AreEqual(
            new[] { "ado-child", "ado-grandchild" },
            result.Preflight!.Descendants.Select(task => task.Id).ToArray());
    }

    [TestMethod]
    public void PreflightTaskDeletion_ReportsEveryOwnedArtifactInTheSubtree()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        _vault.Save(new GlassworkTask { Id = "child", Title = "Child", Parent = "root" });
        var rootArtifacts = Path.Combine(_todoPath, "root.artifacts");
        var nestedArtifacts = Path.Combine(rootArtifacts, "nested");
        var childArtifacts = Path.Combine(_todoPath, "child.artifacts");
        Directory.CreateDirectory(nestedArtifacts);
        Directory.CreateDirectory(childArtifacts);
        File.WriteAllText(Path.Combine(rootArtifacts, "plan.md"), "# Plan");
        File.WriteAllBytes(Path.Combine(nestedArtifacts, "diagram.png"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(childArtifacts, "notes.txt"), "notes");

        var result = _mutations.PreflightTaskDeletion("root");

        CollectionAssert.AreEqual(
            new[]
            {
                "wiki/todo/child.artifacts/notes.txt",
                "wiki/todo/root.artifacts/nested/diagram.png",
                "wiki/todo/root.artifacts/plan.md",
            },
            result.Preflight!.Artifacts.Select(artifact => artifact.VaultRelativePath).ToArray());
        CollectionAssert.AreEqual(
            new[] { "child", "root", "root" },
            result.Preflight.Artifacts.Select(artifact => artifact.TaskId).ToArray());
    }

    [TestMethod]
    public void PreflightTaskDeletion_FindsExactInboundLinksAcrossVaultAndTaskPages()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        _vault.Save(new GlassworkTask { Id = "child", Title = "Child title", Parent = "root" });
        _vault.Save(new GlassworkTask
        {
            Id = "linking-task",
            Title = "Linking task",
            Description = "Keep context for [[root]].",
        });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        File.WriteAllText(
            Path.Combine(concepts, "links.md"),
            "Bare [[root]], alias [[root|Root alias]], child [[child]], unrelated [[other]].");
        File.WriteAllText(Path.Combine(_todoPath, "_index.md"), "Generated [[root]].");

        var result = _mutations.PreflightTaskDeletion("root");

        CollectionAssert.AreEqual(
            new[]
            {
                "wiki/concepts/links.md:3",
                "wiki/todo/linking-task.md:1",
            },
            result.Preflight!.BacklinkPages
                .Select(page => $"{page.VaultRelativePath}:{page.ReplacementCount}")
                .ToArray());
    }

    [TestMethod]
    public void DeleteTask_RequiresMutationRevisionAndExactTitleWithoutChangingFiles()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Exact Root Title" });
        var taskPath = Path.Combine(_todoPath, "root.md");
        var original = File.ReadAllBytes(taskPath);
        var revision = ResourceMutationService.Revision(original);

        var missingMutation = _mutations.DeleteTask(
            null,
            "root",
            revision,
            "Exact Root Title",
            cascadeChildren: false);
        var missingRevision = _mutations.DeleteTask(
            "delete-missing-revision",
            "root",
            null,
            "Exact Root Title",
            cascadeChildren: false);
        var missingTitle = _mutations.DeleteTask(
            "delete-missing-title",
            "root",
            revision,
            null,
            cascadeChildren: false);

        Assert.AreEqual("precondition_required", missingMutation.Outcome);
        Assert.AreEqual("precondition_required", missingRevision.Outcome);
        Assert.AreEqual("precondition_required", missingTitle.Outcome);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(taskPath));
    }

    [TestMethod]
    public void DeleteTask_RejectsAStaleResourceRevisionWithoutChangingFiles()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Original title" });
        var expectedRevision = _vault.Load("root")!.ResourceRevision;
        var changed = _vault.Load("root")!;
        changed.Title = "Changed elsewhere";
        _vault.Save(changed);
        var taskPath = Path.Combine(_todoPath, "root.md");
        var changedBytes = File.ReadAllBytes(taskPath);

        var result = _mutations.DeleteTask(
            "delete-stale",
            "root",
            expectedRevision,
            "Original title",
            cascadeChildren: false);

        Assert.AreEqual("conflict", result.Outcome);
        Assert.AreEqual<string?>(expectedRevision, result.ExpectedRevision);
        Assert.AreEqual(ResourceMutationService.Revision(changedBytes), result.CurrentRevision);
        CollectionAssert.AreEqual(changedBytes, File.ReadAllBytes(taskPath));
    }

    [TestMethod]
    public void DeleteTask_RequiresAnOrdinalExactTitleMatchWithoutChangingFiles()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Exact Root Title" });
        var taskPath = Path.Combine(_todoPath, "root.md");
        var bytes = File.ReadAllBytes(taskPath);

        var result = _mutations.DeleteTask(
            "delete-title-mismatch",
            "root",
            ResourceMutationService.Revision(bytes),
            "exact root title",
            cascadeChildren: false);

        Assert.AreEqual("validation_error", result.Outcome);
        StringAssert.Contains(result.Error, "confirm_title");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(taskPath));
    }

    [TestMethod]
    public void DeleteTask_WithoutCascadeReturnsEveryDescendantIdAndDoesNotMutate()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        _vault.Save(new GlassworkTask { Id = "child-b", Title = "Child B", Parent = "root" });
        _vault.Save(new GlassworkTask { Id = "child-a", Title = "Child A", Parent = "root" });
        _vault.Save(new GlassworkTask { Id = "grandchild", Title = "Grandchild", Parent = "child-a" });
        var root = _vault.Load("root")!;

        var result = _mutations.DeleteTask(
            "delete-needs-cascade",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("descendants_require_cascade", result.Outcome);
        CollectionAssert.AreEqual(
            new[] { "child-a", "child-b", "grandchild" },
            result.DeletionPreflight!.Descendants.Select(task => task.Id).ToArray());
        Assert.IsTrue(_vault.Exists("root"));
        Assert.IsTrue(_vault.Exists("child-a"));
        Assert.IsTrue(_vault.Exists("grandchild"));
    }

    [TestMethod]
    public void DeleteTask_PermanentlyDeletesTheTaskAndReturnsACompleteReport()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        var root = _vault.Load("root")!;
        var deletedEvents = new List<string>();
        _vault.TaskDeleted += (_, taskId) => deletedEvents.Add(taskId);

        var result = _mutations.DeleteTask(
            "delete-root",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("applied", result.Outcome);
        Assert.IsNotNull(result.DeletionReport);
        CollectionAssert.AreEqual(
            new[] { "root" },
            result.DeletionReport.DeletedTasks.Select(task => task.Id).ToArray());
        Assert.AreEqual("not_required", result.DeletionReport.RecoveryOutcome);
        Assert.IsFalse(_vault.Exists("root"));
        CollectionAssert.AreEqual(new[] { "root" }, deletedEvents);
        Assert.IsFalse(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    [TestMethod]
    public void DeleteTask_WithCascadeDeletesTheFullSubtreeAndOwnedArtifactFolders()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        _vault.Save(new GlassworkTask { Id = "child-b", Title = "Child B", Parent = "root" });
        _vault.Save(new GlassworkTask { Id = "child-a", Title = "Child A", Parent = "root" });
        _vault.Save(new GlassworkTask { Id = "grandchild", Title = "Grandchild", Parent = "child-a" });
        var rootArtifacts = Path.Combine(_todoPath, "root.artifacts");
        var childArtifacts = Path.Combine(_todoPath, "child-a.artifacts");
        Directory.CreateDirectory(rootArtifacts);
        Directory.CreateDirectory(childArtifacts);
        File.WriteAllText(Path.Combine(rootArtifacts, "root.md"), "root artifact");
        File.WriteAllText(Path.Combine(childArtifacts, "child.txt"), "child artifact");
        var root = _vault.Load("root")!;
        var preflightRevision = _mutations.PreflightTaskDeletion("root")
            .Preflight!.PreflightRevision;

        var result = _mutations.DeleteTask(
            "delete-cascade",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: true,
            ifPreflightRevision: preflightRevision);

        Assert.AreEqual("applied", result.Outcome);
        CollectionAssert.AreEqual(
            new[] { "root", "child-a", "child-b", "grandchild" },
            result.DeletionReport!.DeletedTasks.Select(task => task.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "child-a", "child-b", "grandchild" },
            result.DeletionReport.Descendants.Select(task => task.Id).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "wiki/todo/child-a.artifacts/child.txt",
                "wiki/todo/root.artifacts/root.md",
            },
            result.DeletionReport.RemovedArtifacts
                .Select(artifact => artifact.VaultRelativePath)
                .ToArray());
        Assert.IsFalse(_vault.Exists("root"));
        Assert.IsFalse(_vault.Exists("child-a"));
        Assert.IsFalse(_vault.Exists("child-b"));
        Assert.IsFalse(_vault.Exists("grandchild"));
        Assert.IsFalse(Directory.Exists(rootArtifacts));
        Assert.IsFalse(Directory.Exists(childArtifacts));
    }

    [TestMethod]
    public void DeleteTask_RewritesOnlyExactWikiLinksAndPreservesEncodingAndLineEndings()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        const string original =
            "Bare [[root]].\r\n" +
            "Alias [[root|keep me]].\r\n" +
            "Spaced [[ root | spaced alias ]].\r\n" +
            "Unrelated [[root-other]].\r\n" +
            "Again [[root]].\r\n";
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(pagePath, original, utf8Bom);
        var originalBytes = File.ReadAllBytes(pagePath);
        var root = _vault.Load("root")!;

        var result = _mutations.DeleteTask(
            "delete-rewrite",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("applied", result.Outcome);
        CollectionAssert.AreEqual(
            new[] { "wiki/concepts/links.md:4" },
            result.DeletionReport!.RewrittenBacklinkPages
                .Select(page => $"{page.VaultRelativePath}:{page.ReplacementCount}")
                .ToArray());
        var updatedBytes = File.ReadAllBytes(pagePath);
        CollectionAssert.AreEqual(originalBytes[..3], updatedBytes[..3]);
        Assert.AreEqual(
            "Bare Root title.\r\n" +
            "Alias keep me.\r\n" +
            "Spaced spaced alias.\r\n" +
            "Unrelated [[root-other]].\r\n" +
            "Again Root title.\r\n",
            utf8Bom.GetString(updatedBytes[3..]));
    }

    [TestMethod]
    public void DeleteTask_KeepsTheTaskIndexCoherentForDeletesAndTaskPageRewrites()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        _vault.Save(new GlassworkTask
        {
            Id = "linking-task",
            Title = "Linking task",
            Description = "Context from [[root]].",
        });
        var index = new IndexService(_vault);
        index.EnsureLoaded();
        var root = _vault.Load("root")!;

        var result = _mutations.DeleteTask(
            "delete-index",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("applied", result.Outcome);
        Assert.IsNull(index.ById("root"));
        Assert.AreEqual(
            "Context from Root title.",
            index.ById("linking-task")!.Description.Trim());
    }

    [TestMethod]
    public void DeleteTask_KeepsTheBacklinkIndexCoherentForRewrittenVaultPages()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        _vault.Save(new GlassworkTask { Id = "other", Title = "Other" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        File.WriteAllText(
            Path.Combine(concepts, "links.md"),
            "References [[root]] and [[other]].");
        var backlinks = new BacklinkIndex();
        backlinks.Build(_vaultRoot);
        var mutations = new ResourceMutationService(
            _todoPath,
            _vault,
            backlinkIndex: backlinks);
        var root = _vault.Load("root")!;

        var result = mutations.DeleteTask(
            "delete-backlink-index",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("applied", result.Outcome);
        Assert.IsEmpty(backlinks.GetBacklinks("root"));
        Assert.HasCount(1, backlinks.GetBacklinks("other"));
    }

    [TestMethod]
    public void DeleteTask_OrdinaryFailureRollsBackEveryRewriteAndDeletion()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        _vault.Save(new GlassworkTask { Id = "child", Title = "Child", Parent = "root" });
        _vault.Save(new GlassworkTask
        {
            Id = "linking-task",
            Title = "Linking task",
            Description = "Context from [[root]].",
        });
        var artifacts = Path.Combine(_todoPath, "root.artifacts");
        Directory.CreateDirectory(artifacts);
        var artifactPath = Path.Combine(artifacts, "plan.md");
        File.WriteAllText(artifactPath, "artifact bytes");
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Before [[root]] after.");
        var before = Directory.EnumerateFiles(_vaultRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}.glasswork{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => Path.GetRelativePath(_vaultRoot, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
        var failing = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new ThrowOnceFault(ResourceMutationFailurePoint.AfterReplacementBeforeCommit));
        var deletedEvents = new List<string>();
        _vault.TaskDeleted += (_, taskId) => deletedEvents.Add(taskId);
        var root = _vault.Load("root")!;
        var preflightRevision = failing.PreflightTaskDeletion("root")
            .Preflight!.PreflightRevision;

        Assert.ThrowsExactly<IOException>(() => failing.DeleteTask(
            "delete-rolls-back",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: true,
            ifPreflightRevision: preflightRevision));

        var after = Directory.EnumerateFiles(_vaultRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}.glasswork{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => Path.GetRelativePath(_vaultRoot, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
        foreach (var (path, bytes) in before)
            CollectionAssert.AreEqual(bytes, after[path], path);
        Assert.IsEmpty(deletedEvents);
        Assert.IsFalse(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    [TestMethod]
    public void DeleteTask_CommittedRecoveryFinishesDeterministicallyAndReplaysTheReport()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Before [[root]] after.");
        var root = _vault.Load("root")!;
        var failing = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new ThrowOnceFault(ResourceMutationFailurePoint.AfterCommit));

        Assert.ThrowsExactly<IOException>(() => failing.DeleteTask(
            "delete-response-lost",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false));

        var recovered = new ResourceMutationService(_todoPath, _vault);
        var replay = recovered.DeleteTask(
            "delete-response-lost",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("applied", replay.Outcome);
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual(
            "completed_after_recovery",
            replay.DeletionReport!.RecoveryOutcome);
        Assert.IsFalse(_vault.Exists("root"));
        Assert.AreEqual("Before Root title after.", File.ReadAllText(pagePath));
        Assert.IsFalse(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    [TestMethod]
    public void DeleteTask_ConflictsWhenThePreflightImpactChangesBeforeCommit()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Original [[root]].");
        var root = _vault.Load("root")!;
        var guarded = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new MutateOnceFault(
                ResourceMutationFailurePoint.BeforeFinalValidation,
                () => File.WriteAllText(pagePath, "Changed externally [[root|safe alias]].")));

        var result = guarded.DeleteTask(
            "delete-impact-conflict",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("conflict", result.Outcome);
        Assert.IsTrue(_vault.Exists("root"));
        Assert.AreEqual(
            "Changed externally [[root|safe alias]].",
            File.ReadAllText(pagePath));
        Assert.IsFalse(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    [TestMethod]
    public void DeleteTask_RefusesAConcurrentPageEditAtReplacement()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Original [[root]].");
        var root = _vault.Load("root")!;
        var guarded = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new MutateOnceFault(
                ResourceMutationFailurePoint.DuringReplacement,
                () => File.WriteAllText(pagePath, "Concurrent [[root|preserve me]].")));

        Assert.ThrowsExactly<ResourceRevisionConflictException>(() =>
            guarded.DeleteTask(
                "delete-replacement-conflict",
                "root",
                root.ResourceRevision,
                root.Title,
                cascadeChildren: false));

        Assert.AreEqual("Concurrent [[root|preserve me]].", File.ReadAllText(pagePath));
        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "root.md")));
        Assert.IsTrue(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    [TestMethod]
    public void DeleteTask_RejectsCascadeWhenTheReviewedSubtreeChanged()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root" });
        _vault.Save(new GlassworkTask { Id = "child", Title = "Child", Parent = "root" });
        var root = _vault.Load("root")!;
        var reviewed = _mutations.PreflightTaskDeletion("root").Preflight!;
        _vault.Save(new GlassworkTask
        {
            Id = "new-grandchild",
            Title = "New grandchild",
            Parent = "child",
        });

        var result = _mutations.DeleteTask(
            "delete-stale-preflight",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: true,
            ifPreflightRevision: reviewed.PreflightRevision);

        Assert.AreEqual("conflict", result.Outcome);
        Assert.AreNotEqual(
            reviewed.PreflightRevision,
            result.DeletionPreflight!.PreflightRevision);
        CollectionAssert.Contains(
            result.DeletionPreflight.Descendants.Select(task => task.Id).ToArray(),
            "new-grandchild");
        Assert.IsTrue(_vault.Exists("root"));
        Assert.IsTrue(_vault.Exists("new-grandchild"));
    }

    [TestMethod]
    public void DeleteTask_ReplaysExactlyAndRejectsChangedMutationIntent()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var root = _vault.Load("root")!;

        var first = _mutations.DeleteTask(
            "delete-idempotent",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);
        var replay = _mutations.DeleteTask(
            "delete-idempotent",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);
        var reused = _mutations.DeleteTask(
            "delete-idempotent",
            "different-task",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("applied", first.Outcome);
        Assert.AreEqual("applied", replay.Outcome);
        Assert.IsTrue(replay.Replayed);
        CollectionAssert.AreEqual(
            first.DeletionReport!.DeletedTasks.Select(task => task.Id).ToArray(),
            replay.DeletionReport!.DeletedTasks.Select(task => task.Id).ToArray());
        Assert.AreEqual("mutation_id_reused", reused.Outcome);
    }

    [TestMethod]
    public void StartupRecovery_FinishesACommittedDeletionJournal()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var rootPath = Path.Combine(_todoPath, "root.md");
        var rootBytes = File.ReadAllBytes(rootPath);
        var revision = ResourceMutationService.Revision(rootBytes);
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Before [[root]] after.");
        var pageBytes = File.ReadAllBytes(pagePath);
        var updatedPageBytes = Encoding.UTF8.GetBytes("Before Root title after.");
        const string mutationId = "delete-startup-recovery";
        var payload =
            """{"cascade_children":false,"confirm_title":"Root title","if_preflight_revision":null}""";
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{mutationId}\ndelete_task\nroot\n{revision}\n{payload}")))
            .ToLowerInvariant();
        const string operationId = "manual-recovery";
        var operationDirectory = Path.Combine(
            _todoPath,
            ".glasswork",
            "deletion-operations",
            operationId);
        Directory.CreateDirectory(Path.Combine(operationDirectory, "files"));
        File.WriteAllBytes(Path.Combine(operationDirectory, "files", "0000.original"), pageBytes);
        File.WriteAllBytes(Path.Combine(operationDirectory, "files", "0000.updated"), updatedPageBytes);
        File.WriteAllBytes(Path.Combine(operationDirectory, "files", "0001.original"), rootBytes);
        var task = new TaskDeletionTask("root", "Root title", revision);
        var preflight = new TaskDeletionPreflight(task, [], [], [
            new TaskDeletionBacklinkPage("wiki/concepts/links.md", 1),
        ], [], "dpr1-manual-commit");
        var report = new TaskDeletionReport(
            [task],
            [],
            [],
            preflight.BacklinkPages,
            [],
            "not_required");
        var journalPath = Path.Combine(_todoPath, ".glasswork", "mutation-journal.json");
        File.WriteAllText(journalPath, JsonSerializer.Serialize(new
        {
            Kind = "task_deletion",
            MutationId = mutationId,
            RequestHash = requestHash,
            ExpectedRevision = revision,
            Committed = true,
            OperationId = operationId,
            Preflight = preflight,
            Report = report,
            Files = new object[]
            {
                new
                {
                    VaultRelativePath = "wiki/concepts/links.md",
                    BackupRelativePath = "files/0000.original",
                    StagedRelativePath = "files/0000.updated",
                    Action = "rewrite",
                    TaskId = (string?)null,
                    OriginalRevision = ResourceMutationService.Revision(pageBytes),
                    StagedRevision = ResourceMutationService.Revision(updatedPageBytes),
                },
                new
                {
                    VaultRelativePath = "wiki/todo/root.md",
                    BackupRelativePath = "files/0001.original",
                    StagedRelativePath = (string?)null,
                    Action = "delete",
                    TaskId = "root",
                    OriginalRevision = revision,
                    StagedRevision = (string?)null,
                },
            },
            Directories = Array.Empty<object>(),
        }));

        var recoveredVault = new VaultService(
            _todoPath,
            new SelfWriteCoordinator(_todoPath));
        var recovered = new ResourceMutationService(_todoPath, recoveredVault);
        var replay = recovered.DeleteTask(
            mutationId,
            "root",
            revision,
            "Root title",
            cascadeChildren: false);

        Assert.IsFalse(recoveredVault.Exists("root"));
        Assert.AreEqual("Before Root title after.", File.ReadAllText(pagePath));
        Assert.IsFalse(File.Exists(journalPath));
        Assert.IsFalse(Directory.Exists(operationDirectory));
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual(
            "completed_after_recovery",
            replay.DeletionReport!.RecoveryOutcome);
    }

    [TestMethod]
    public void DeleteTask_FailureBeforeJournalCleansStagedBackupsWithoutMutating()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var root = _vault.Load("root")!;
        var failing = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new ThrowOnceFault(ResourceMutationFailurePoint.BeforeJournal));

        Assert.ThrowsExactly<IOException>(() => failing.DeleteTask(
            "delete-before-journal",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false));

        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "root.md")));
        var operationsPath = Path.Combine(
            _todoPath,
            ".glasswork",
            "deletion-operations");
        Assert.IsFalse(Directory.Exists(operationsPath)
            && Directory.EnumerateFileSystemEntries(operationsPath).Any());
    }

    [TestMethod]
    public void StartupRecovery_RemovesOrphanedBackupDirectoriesWithoutAJournal()
    {
        var orphan = Path.Combine(
            _todoPath,
            ".glasswork",
            "deletion-operations",
            "orphan");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "backup.bin"), "orphan");

        _ = new ResourceMutationService(
            _todoPath,
            new VaultService(_todoPath, new SelfWriteCoordinator(_todoPath)));

        Assert.IsFalse(Directory.Exists(orphan));
    }

    [TestMethod]
    public void StartupRecovery_RollsBackAnUncommittedDeletionJournal()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var rootPath = Path.Combine(_todoPath, "root.md");
        var rootBytes = File.ReadAllBytes(rootPath);
        var revision = ResourceMutationService.Revision(rootBytes);
        const string operationId = "manual-rollback";
        var operationDirectory = Path.Combine(
            _todoPath,
            ".glasswork",
            "deletion-operations",
            operationId);
        Directory.CreateDirectory(Path.Combine(operationDirectory, "files"));
        File.WriteAllBytes(Path.Combine(operationDirectory, "files", "0000.original"), rootBytes);
        var task = new TaskDeletionTask("root", "Root title", revision);
        var preflight = new TaskDeletionPreflight(
            task,
            [],
            [],
            [],
            [],
            "dpr1-manual-rollback");
        var report = new TaskDeletionReport([task], [], [], [], [], "not_required");
        var journalPath = Path.Combine(_todoPath, ".glasswork", "mutation-journal.json");
        File.WriteAllText(journalPath, JsonSerializer.Serialize(new
        {
            Kind = "task_deletion",
            MutationId = "delete-uncommitted",
            RequestHash = "request-hash",
            ExpectedRevision = revision,
            Committed = false,
            OperationId = operationId,
            Preflight = preflight,
            Report = report,
            Files = new[]
            {
                new
                {
                    VaultRelativePath = "wiki/todo/root.md",
                    BackupRelativePath = "files/0000.original",
                    StagedRelativePath = (string?)null,
                    Action = "delete",
                    TaskId = "root",
                    OriginalRevision = revision,
                    StagedRevision = (string?)null,
                },
            },
            Directories = Array.Empty<object>(),
        }));
        File.Delete(rootPath);

        var recoveredVault = new VaultService(
            _todoPath,
            new SelfWriteCoordinator(_todoPath));
        _ = new ResourceMutationService(_todoPath, recoveredVault);

        Assert.IsNotNull(recoveredVault.Load("root"));
        CollectionAssert.AreEqual(rootBytes, File.ReadAllBytes(rootPath));
        Assert.IsFalse(File.Exists(journalPath));
        Assert.IsFalse(Directory.Exists(operationDirectory));
    }

    [TestMethod]
    public void StartupRecovery_RejectsADeletionJournalThatEscapesHiddenStaging()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var task = _vault.Load("root")!;
        var deletionTask = new TaskDeletionTask("root", task.Title, task.ResourceRevision);
        var preflight = new TaskDeletionPreflight(
            deletionTask,
            [],
            [],
            [],
            [],
            "dpr1-invalid-journal");
        var report = new TaskDeletionReport(
            [deletionTask],
            [],
            [],
            [],
            [],
            "not_required");
        var journalPath = Path.Combine(_todoPath, ".glasswork", "mutation-journal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.WriteAllText(journalPath, JsonSerializer.Serialize(new
        {
            Kind = "task_deletion",
            MutationId = "malicious-delete",
            RequestHash = "hash",
            ExpectedRevision = task.ResourceRevision,
            Committed = false,
            OperationId = "../escape",
            Preflight = preflight,
            Report = report,
            Files = Array.Empty<object>(),
            Directories = Array.Empty<object>(),
        }));
        var recoveringVault = new VaultService(
            _todoPath,
            new SelfWriteCoordinator(_todoPath));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new ResourceMutationService(_todoPath, recoveringVault));

        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "root.md")));
        Assert.IsTrue(File.Exists(journalPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(_todoPath, ".glasswork", "escape")));
    }

    [TestMethod]
    public void StartupRecovery_RetainsATornDeletionJournalAndItsBackups()
    {
        var operationDirectory = Path.Combine(
            _todoPath,
            ".glasswork",
            "deletion-operations",
            "blocked-recovery");
        Directory.CreateDirectory(operationDirectory);
        var backupPath = Path.Combine(operationDirectory, "backup.bin");
        File.WriteAllText(backupPath, "must survive");
        var journalPath = Path.Combine(_todoPath, ".glasswork", "mutation-journal.json");
        File.WriteAllText(journalPath, """{"Kind":"task_deletion","MutationId":"torn""");
        var recoveringVault = new VaultService(
            _todoPath,
            new SelfWriteCoordinator(_todoPath));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new ResourceMutationService(_todoPath, recoveringVault));

        Assert.IsTrue(File.Exists(journalPath));
        Assert.IsTrue(File.Exists(backupPath));
        Assert.AreEqual("must survive", File.ReadAllText(backupPath));
    }

    [TestMethod]
    public void StartupRecovery_RejectsAnIncompleteDeletionManifest()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var root = _vault.Load("root")!;
        var deletionTask = new TaskDeletionTask("root", root.Title, root.ResourceRevision);
        var preflight = new TaskDeletionPreflight(
            deletionTask,
            [],
            [],
            [],
            [],
            "dpr1-incomplete");
        var report = new TaskDeletionReport(
            [deletionTask],
            [],
            [],
            [],
            [],
            "not_required");
        var operationDirectory = Path.Combine(
            _todoPath,
            ".glasswork",
            "deletion-operations",
            "incomplete");
        Directory.CreateDirectory(operationDirectory);
        var backupPath = Path.Combine(operationDirectory, "orphan-backup.bin");
        File.WriteAllText(backupPath, "retain");
        var journalPath = Path.Combine(_todoPath, ".glasswork", "mutation-journal.json");
        File.WriteAllText(journalPath, JsonSerializer.Serialize(new
        {
            Kind = "task_deletion",
            MutationId = "incomplete-delete",
            RequestHash = "hash",
            ExpectedRevision = root.ResourceRevision,
            Committed = false,
            OperationId = "incomplete",
            Preflight = preflight,
            Report = report,
            Files = Array.Empty<object>(),
            Directories = Array.Empty<object>(),
        }));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new ResourceMutationService(
                _todoPath,
                new VaultService(_todoPath, new SelfWriteCoordinator(_todoPath))));

        Assert.IsTrue(File.Exists(journalPath));
        Assert.AreEqual("retain", File.ReadAllText(backupPath));
    }

    [TestMethod]
    public void PreflightTaskDeletion_SkipsUndecodableMarkdownThatCannotTargetTheTask()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var legacyDirectory = Path.Combine(_vaultRoot, "wiki", "legacy");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllBytes(
            Path.Combine(legacyDirectory, "windows-1252.md"),
            [0x93, 0x4C, 0x65, 0x67, 0x61, 0x63, 0x79, 0x94]);

        var result = _mutations.PreflightTaskDeletion("root");

        Assert.AreEqual("ready", result.Outcome);
        Assert.IsEmpty(result.Preflight!.BacklinkPages);
    }

    [TestMethod]
    public void PreflightTaskDeletion_FailsClosedForAnUndecodableCandidateLinkPage()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var legacyDirectory = Path.Combine(_vaultRoot, "wiki", "legacy");
        Directory.CreateDirectory(legacyDirectory);
        var pagePath = Path.Combine(legacyDirectory, "candidate.md");
        File.WriteAllBytes(
            pagePath,
            Encoding.ASCII.GetBytes("[[root]] ").Append((byte)0x93).ToArray());

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            _mutations.PreflightTaskDeletion("root"));

        StringAssert.Contains(error.Message, "wiki/legacy/candidate.md");
        Assert.IsTrue(_vault.Exists("root"));
        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("[[root]] ").Append((byte)0x93).ToArray(),
            File.ReadAllBytes(pagePath));
    }

    [TestMethod]
    public void PreflightTaskDeletion_RejectsFilenameAndFrontmatterIdMismatch()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        File.Move(
            Path.Combine(_todoPath, "root.md"),
            Path.Combine(_todoPath, "wrong-file.md"));

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            _mutations.PreflightTaskDeletion("root"));

        StringAssert.Contains(error.Message, "mismatched id");
        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "wrong-file.md")));
    }

    [TestMethod]
    public void DeleteTask_RewritesCanonicalWikiLinksWithUnicodeWhitespace()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "unicode-space.md");
        File.WriteAllText(
            pagePath,
            "Before [[\u00a0root\u00a0]] after.",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var root = _vault.Load("root")!;

        var result = _mutations.DeleteTask(
            "delete-unicode-space",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false);

        Assert.AreEqual("applied", result.Outcome);
        Assert.AreEqual("Before Root title after.", File.ReadAllText(pagePath));
        Assert.HasCount(1, result.DeletionReport!.RewrittenBacklinkPages);
    }

    [TestMethod]
    public void StartupRollback_RefusesToOverwriteAPostCrashVaultEdit()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Before [[root]] after.");
        var root = _vault.Load("root")!;
        var interrupted = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new InterruptDeleteRecoveryFault());

        Assert.ThrowsExactly<IOException>(() => interrupted.DeleteTask(
            "delete-interrupted",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false));
        File.WriteAllText(pagePath, "External edit [[root|preserve me]].");

        Assert.ThrowsExactly<ResourceRevisionConflictException>(() =>
            _ = new ResourceMutationService(
                _todoPath,
                new VaultService(_todoPath, new SelfWriteCoordinator(_todoPath))));

        Assert.AreEqual("External edit [[root|preserve me]].", File.ReadAllText(pagePath));
        Assert.IsTrue(File.Exists(Path.Combine(_todoPath, "root.md")));
        Assert.IsTrue(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    [TestMethod]
    public void StartupRollback_RefreshesTheBacklinkIndexAfterRestoringPages()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        var pagePath = Path.Combine(concepts, "links.md");
        File.WriteAllText(pagePath, "Before [[root]] after.");
        var root = _vault.Load("root")!;
        var interrupted = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new InterruptDeleteRecoveryFault());

        Assert.ThrowsExactly<IOException>(() => interrupted.DeleteTask(
            "delete-interrupted-index",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false));
        Assert.AreEqual("Before Root title after.", File.ReadAllText(pagePath));
        var backlinks = new BacklinkIndex();
        backlinks.Build(_vaultRoot);
        Assert.IsEmpty(backlinks.GetBacklinks("root"));

        _ = new ResourceMutationService(
            _todoPath,
            new VaultService(_todoPath, new SelfWriteCoordinator(_todoPath)),
            backlinkIndex: backlinks);

        Assert.AreEqual("Before [[root]] after.", File.ReadAllText(pagePath));
        Assert.HasCount(1, backlinks.GetBacklinks("root"));
    }

    [TestMethod]
    public void StartupRollback_RefusesToOverwriteARecreatedArtifactDirectory()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var artifacts = Path.Combine(_todoPath, "root.artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "original.md"), "original");
        var root = _vault.Load("root")!;
        var interrupted = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new InterruptDeleteRecoveryFault());

        Assert.ThrowsExactly<IOException>(() => interrupted.DeleteTask(
            "delete-interrupted-artifacts",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false));
        Directory.CreateDirectory(artifacts);
        var externalPath = Path.Combine(artifacts, "external.md");
        File.WriteAllText(externalPath, "external");

        Assert.ThrowsExactly<ResourceRevisionConflictException>(() =>
            _ = new ResourceMutationService(
                _todoPath,
                new VaultService(_todoPath, new SelfWriteCoordinator(_todoPath))));

        Assert.AreEqual("external", File.ReadAllText(externalPath));
        Assert.IsTrue(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    [TestMethod]
    [DataRow("wiki", "todo")]
    [DataRow("Wiki", "Todo")]
    public void StartupRecovery_RollsBackArtifactDeletionWithConfiguredTaskDirectoryCasing(
        string wikiDirectoryName,
        string todoDirectoryName)
    {
        var vaultRoot = Path.Combine(
            Path.GetTempPath(),
            "glasswork-delete-case-tests",
            Guid.NewGuid().ToString("N"));
        var todoPath = Path.Combine(vaultRoot, wikiDirectoryName, todoDirectoryName);
        Directory.CreateDirectory(todoPath);
        try
        {
            var vault = new VaultService(
                todoPath,
                new SelfWriteCoordinator(todoPath));
            var task = new GlassworkTask { Id = "root", Title = "Root title" };
            vault.Save(task);
            var artifactDirectory = Path.Combine(todoPath, "root.artifacts");
            Directory.CreateDirectory(artifactDirectory);
            var artifactPath = Path.Combine(artifactDirectory, "plan.md");
            File.WriteAllText(artifactPath, "artifact");
            var root = vault.Load("root")!;
            var interrupted = new ResourceMutationService(
                todoPath,
                vault,
                faults: new InterruptDeleteRecoveryFault());
            var preflight = interrupted.PreflightTaskDeletion("root").Preflight!;
            Assert.AreEqual(
                $"{wikiDirectoryName}/{todoDirectoryName}/root.artifacts",
                preflight.ArtifactDirectories.Single());

            Assert.ThrowsExactly<IOException>(() => interrupted.DeleteTask(
                "delete-case-recovery",
                "root",
                root.ResourceRevision,
                root.Title,
                cascadeChildren: false));

            var recoveredVault = new VaultService(
                todoPath,
                new SelfWriteCoordinator(todoPath));
            _ = new ResourceMutationService(todoPath, recoveredVault);

            Assert.IsNotNull(recoveredVault.Load("root"));
            Assert.AreEqual("artifact", File.ReadAllText(artifactPath));
            Assert.IsFalse(File.Exists(
                Path.Combine(todoPath, ".glasswork", "mutation-journal.json")));
            var operationsPath = Path.Combine(
                todoPath,
                ".glasswork",
                "deletion-operations");
            Assert.IsFalse(Directory.Exists(operationsPath)
                && Directory.EnumerateFileSystemEntries(operationsPath).Any());
        }
        finally
        {
            if (Directory.Exists(vaultRoot))
                Directory.Delete(vaultRoot, recursive: true);
        }
    }

    [TestMethod]
    public void StartupRecovery_RejectsATornStagedBackup()
    {
        _vault.Save(new GlassworkTask { Id = "root", Title = "Root title" });
        var concepts = Path.Combine(_vaultRoot, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        File.WriteAllText(Path.Combine(concepts, "links.md"), "Before [[root]] after.");
        var root = _vault.Load("root")!;
        var interrupted = new ResourceMutationService(
            _todoPath,
            _vault,
            faults: new InterruptDeleteRecoveryFault());

        Assert.ThrowsExactly<IOException>(() => interrupted.DeleteTask(
            "delete-torn-backup",
            "root",
            root.ResourceRevision,
            root.Title,
            cascadeChildren: false));
        var operationDirectory = Directory.GetDirectories(
            Path.Combine(_todoPath, ".glasswork", "deletion-operations")).Single();
        var backupPath = Directory.GetFiles(
            Path.Combine(operationDirectory, "files"),
            "*.original").First();
        File.WriteAllText(backupPath, "truncated");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            _ = new ResourceMutationService(
                _todoPath,
                new VaultService(_todoPath, new SelfWriteCoordinator(_todoPath))));

        Assert.AreEqual("truncated", File.ReadAllText(backupPath));
        Assert.IsTrue(File.Exists(
            Path.Combine(_todoPath, ".glasswork", "mutation-journal.json")));
    }

    private sealed class ThrowOnceFault(ResourceMutationFailurePoint point)
        : IResourceMutationFaultInjector
    {
        private bool _thrown;

        public void ThrowIfInjected(ResourceMutationFailurePoint candidate)
        {
            if (_thrown || candidate != point)
                return;
            _thrown = true;
            throw new IOException("Injected Task deletion failure.");
        }
    }

    private sealed class MutateOnceFault(
        ResourceMutationFailurePoint point,
        Action mutate)
        : IResourceMutationFaultInjector
    {
        private bool _mutated;

        public void ThrowIfInjected(ResourceMutationFailurePoint candidate)
        {
            if (_mutated || candidate != point)
                return;
            _mutated = true;
            mutate();
        }
    }

    private sealed class InterruptDeleteRecoveryFault : IResourceMutationFaultInjector
    {
        private int _replacementCount;
        private bool _recoveryInterrupted;

        public void ThrowIfInjected(ResourceMutationFailurePoint candidate)
        {
            if (candidate == ResourceMutationFailurePoint.DuringReplacement
                && ++_replacementCount == 2)
            {
                throw new IOException("Simulated process interruption during deletion.");
            }
            if (candidate == ResourceMutationFailurePoint.DuringRecovery
                && !_recoveryInterrupted)
            {
                _recoveryInterrupted = true;
                throw new IOException("Simulated process interruption during recovery.");
            }
        }
    }
}
