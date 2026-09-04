using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskTypeBackfillServiceVaultTests
{
    private string _todo = null!;

    [TestInitialize]
    public void Setup()
    {
        _todo = Path.Combine(Path.GetTempPath(), "gw-backfill-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_todo);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_todo))
            Directory.Delete(_todo, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_todo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.ReplaceLineEndings("\n"));
    }

    [TestMethod]
    public void Inventory_EnumeratesRootAndDone_ExcludesArtifacts_ReportsTypeAndAdoState()
    {
        Write("pbi-file.md", """
            ---
            id: pbi-file
            status: todo
            priority: medium
            ado_link: 14480984
            ---

            ADO 14480984 — x

            ## Subtasks
            """);
        Write("done/done-file.md", """
            ---
            id: done-file
            status: done
            ado_link: 32681761
            ---

            ## Subtasks
            """);
        Write("typed.md", """
            ---
            id: typed
            status: todo
            type: pbi
            ado_link: 37076384
            ---

            ## Subtasks
            """);
        // A file inside a *.artifacts/ subfolder must NOT be enumerated.
        Write("pbi-file.artifacts/plan.md", "---\nid: plan\n---\n\nADO 99999999 — nope\n");

        var inv = new TaskTypeBackfillService(_todo).Inventory();
        var byPath = inv.ToDictionary(i => i.RelativePath);

        CollectionAssert.AreEquivalent(
            new[] { "pbi-file.md", "done/done-file.md", "typed.md" },
            byPath.Keys.ToArray());

        Assert.AreEqual(AdoIdStatus.Resolved, byPath["pbi-file.md"].Ado.Status);
        Assert.AreEqual(14480984, byPath["pbi-file.md"].Ado.Id);
        Assert.IsFalse(byPath["pbi-file.md"].HasType);
        Assert.IsNull(byPath["pbi-file.md"].RawType);
        Assert.AreEqual("task", byPath["pbi-file.md"].NormalizedType);

        Assert.IsTrue(byPath["typed.md"].HasType);
        Assert.AreEqual("pbi", byPath["typed.md"].RawType);
        Assert.AreEqual("pbi", byPath["typed.md"].NormalizedType);
    }

    // ----- Run (dry-run / apply) -----

    private const string PbiFile = """
        ---
        id: pbi-file
        status: todo
        priority: medium
        ado_link: 14480984
        ---

        ADO 14480984 — x

        ## Subtasks
        """;

    [TestMethod]
    public void Run_Apply_StampsClassifiedFile_AndRegistersSelfWrite()
    {
        Write("pbi-file.md", PbiFile);
        var selfWrites = new SelfWriteCoordinator(_todo);
        var svc = new TaskTypeBackfillService(_todo, selfWrites);

        var report = svc.Run([new BackfillClassification("pbi-file.md", 14480984, "pbi")], dryRun: false);

        CollectionAssert.Contains(report.Stamped.ToArray(), "pbi-file.md");
        var written = File.ReadAllText(Path.Combine(_todo, "pbi-file.md"));
        StringAssert.Contains(written, "priority: medium\ntype: pbi\n");
        StringAssert.Contains(written, "ado_link: 14480984"); // legacy field preserved
        Assert.IsTrue(selfWrites.IsOwnProcessWrite(Path.Combine(_todo, "pbi-file.md")),
            "the write must be registered with SelfWriteCoordinator (hard rule 5)");
    }

    [TestMethod]
    public void Run_DryRun_ReportsWouldStamp_ButWritesNothing()
    {
        Write("pbi-file.md", PbiFile);
        var svc = new TaskTypeBackfillService(_todo);

        var report = svc.Run([new BackfillClassification("pbi-file.md", 14480984, "pbi")], dryRun: true);

        CollectionAssert.Contains(report.Stamped.ToArray(), "pbi-file.md");
        Assert.IsTrue(report.DryRun);
        var onDisk = File.ReadAllText(Path.Combine(_todo, "pbi-file.md"));
        Assert.DoesNotContain("type: pbi", onDisk, "dry-run must not modify the file");
    }

    [TestMethod]
    public void Run_IsIdempotent_SecondRunIsNoOp()
    {
        Write("pbi-file.md", PbiFile);
        var svc = new TaskTypeBackfillService(_todo);
        var classification = new[] { new BackfillClassification("pbi-file.md", 14480984, "pbi") };

        var first = svc.Run(classification, dryRun: false);
        Assert.HasCount(1, first.Stamped);
        var afterFirst = File.ReadAllText(Path.Combine(_todo, "pbi-file.md"));

        // Second dry-run: nothing left to do.
        var secondDry = svc.Run(classification, dryRun: true);
        Assert.IsEmpty(secondDry.Stamped);
        CollectionAssert.Contains(secondDry.SkippedAlreadyTyped.ToArray(), "pbi-file.md");

        // Second apply: writes nothing; file byte-for-byte unchanged.
        var secondApply = svc.Run(classification, dryRun: false);
        Assert.IsEmpty(secondApply.Stamped);
        Assert.AreEqual(afterFirst, File.ReadAllText(Path.Combine(_todo, "pbi-file.md")));
    }

    [TestMethod]
    public void Run_RejectsInvalidClassifications_InPreflight()
    {
        Write("pbi-file.md", PbiFile);
        var svc = new TaskTypeBackfillService(_todo);

        var report = svc.Run(
        [
            new BackfillClassification("does-not-exist.md", 111, "pbi"),
            new BackfillClassification("pbi-file.md", 14480984, "task"), // task not allowed
        ], dryRun: false);

        Assert.IsEmpty(report.Stamped);
        var rejectedPaths = report.Invalid.Select(r => r.RelativePath).ToArray();
        CollectionAssert.Contains(rejectedPaths, "does-not-exist.md");
        CollectionAssert.Contains(rejectedPaths, "pbi-file.md");
    }

    [TestMethod]
    public void Run_RejectsDuplicateClassifications()
    {
        Write("pbi-file.md", PbiFile);
        var svc = new TaskTypeBackfillService(_todo);

        var report = svc.Run(
        [
            new BackfillClassification("pbi-file.md", 14480984, "pbi"),
            new BackfillClassification("pbi-file.md", 14480984, "pbi"),
        ], dryRun: false);

        Assert.IsEmpty(report.Stamped);
        CollectionAssert.Contains(report.Invalid.Select(r => r.RelativePath).ToArray(), "pbi-file.md");
        Assert.DoesNotContain("type: pbi", File.ReadAllText(Path.Combine(_todo, "pbi-file.md")));
    }

    [TestMethod]
    public void Run_SkipsDrift_WhenClassifiedAdoIdNoLongerMatches()
    {
        Write("pbi-file.md", PbiFile); // real ado id is 14480984
        var svc = new TaskTypeBackfillService(_todo);

        var report = svc.Run([new BackfillClassification("pbi-file.md", 99999999, "pbi")], dryRun: false);

        Assert.IsEmpty(report.Stamped);
        CollectionAssert.Contains(report.SkippedDrift.ToArray(), "pbi-file.md");
        Assert.DoesNotContain("type: pbi", File.ReadAllText(Path.Combine(_todo, "pbi-file.md")));
    }

    [TestMethod]
    public void Run_Apply_SkipsAndReportsConflict_WhenFileChangesBetweenReadAndWrite()
    {
        Write("pbi-file.md", PbiFile);
        var fullPath = Path.Combine(_todo, "pbi-file.md");
        var svc = new TaskTypeBackfillService(_todo);

        // Simulate a concurrent vault edit (Obsidian / the app) landing after Run reads the
        // file but before it writes — exercised via the before-write test seam.
        svc.BeforeWriteHook = path =>
            File.WriteAllText(path, "---\nid: pbi-file\nstatus: done\n---\n\nconcurrently edited\n");

        var report = svc.Run([new BackfillClassification("pbi-file.md", 14480984, "pbi")], dryRun: false);

        Assert.IsEmpty(report.Stamped, "must not overwrite a concurrently edited file");
        CollectionAssert.Contains(report.SkippedConflict.ToArray(), "pbi-file.md");
        var onDisk = File.ReadAllText(fullPath);
        StringAssert.Contains(onDisk, "concurrently edited"); // the concurrent edit survived
        Assert.DoesNotContain("type: pbi", onDisk);
    }

    [TestMethod]
    public void Run_Apply_MalformedFrontmatterResolvedByBody_IsUnstampable_NotAlreadyTyped()
    {
        // Opening `---` but no closing delimiter: ResolveAdoId still finds the body id, but
        // StampType has no frontmatter span to insert into. It must be surfaced as
        // unstampable, not silently hidden in the already-typed bucket.
        Write("broken.md",
            "---\nid: broken\npriority: medium\n\nADO 14480984 — https://msazure.visualstudio.com/One/_workitems/edit/14480984\n");
        var svc = new TaskTypeBackfillService(_todo);

        var report = svc.Run([new BackfillClassification("broken.md", 14480984, "pbi")], dryRun: false);

        Assert.IsEmpty(report.Stamped);
        CollectionAssert.Contains(report.Unstampable.ToArray(), "broken.md");
        CollectionAssert.DoesNotContain(report.SkippedAlreadyTyped.ToArray(), "broken.md");
    }
}
