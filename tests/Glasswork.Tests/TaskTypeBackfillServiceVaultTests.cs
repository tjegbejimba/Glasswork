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
        Assert.IsFalse(onDisk.Contains("type: pbi"), "dry-run must not modify the file");
    }

    [TestMethod]
    public void Run_IsIdempotent_SecondRunIsNoOp()
    {
        Write("pbi-file.md", PbiFile);
        var svc = new TaskTypeBackfillService(_todo);
        var classification = new[] { new BackfillClassification("pbi-file.md", 14480984, "pbi") };

        var first = svc.Run(classification, dryRun: false);
        Assert.AreEqual(1, first.Stamped.Count);
        var afterFirst = File.ReadAllText(Path.Combine(_todo, "pbi-file.md"));

        // Second dry-run: nothing left to do.
        var secondDry = svc.Run(classification, dryRun: true);
        Assert.AreEqual(0, secondDry.Stamped.Count);
        CollectionAssert.Contains(secondDry.SkippedAlreadyTyped.ToArray(), "pbi-file.md");

        // Second apply: writes nothing; file byte-for-byte unchanged.
        var secondApply = svc.Run(classification, dryRun: false);
        Assert.AreEqual(0, secondApply.Stamped.Count);
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

        Assert.AreEqual(0, report.Stamped.Count);
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

        Assert.AreEqual(0, report.Stamped.Count);
        CollectionAssert.Contains(report.Invalid.Select(r => r.RelativePath).ToArray(), "pbi-file.md");
        Assert.IsFalse(File.ReadAllText(Path.Combine(_todo, "pbi-file.md")).Contains("type: pbi"));
    }

    [TestMethod]
    public void Run_SkipsDrift_WhenClassifiedAdoIdNoLongerMatches()
    {
        Write("pbi-file.md", PbiFile); // real ado id is 14480984
        var svc = new TaskTypeBackfillService(_todo);

        var report = svc.Run([new BackfillClassification("pbi-file.md", 99999999, "pbi")], dryRun: false);

        Assert.AreEqual(0, report.Stamped.Count);
        CollectionAssert.Contains(report.SkippedDrift.ToArray(), "pbi-file.md");
        Assert.IsFalse(File.ReadAllText(Path.Combine(_todo, "pbi-file.md")).Contains("type: pbi"));
    }
}
