using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glasswork.Maintenance.Tests;

[TestClass]
public sealed class ParentMigrationCliTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"glasswork-migration-tests-{Guid.NewGuid():N}");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "basic"), _root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task DryRun_PlansLegacyParentAndPromotionWithoutWritingVault()
    {
        var before = Snapshot(_root);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var output = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual("ready", output.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual(1, output.RootElement.GetProperty("converted_parent_count").GetInt32());
            Assert.AreEqual(1, output.RootElement.GetProperty("promotion_count").GetInt32());
            Assert.AreEqual(0, output.RootElement.GetProperty("blocking_diagnostics").GetArrayLength());
            Assert.IsTrue(File.Exists(planPath));
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var lookup = plan.RootElement.GetProperty("source_kind_lookups")[0];
            Assert.AreEqual("legacy-parent", lookup.GetProperty("task_id").GetString());
            Assert.AreEqual(123, lookup.GetProperty("ado_id").GetInt32());
            Assert.AreEqual("resolved", lookup.GetProperty("outcome").GetString());
            Assert.AreEqual("Product Backlog Item", lookup.GetProperty("source_kind").GetString());
            CollectionAssert.AreEquivalent(before, Snapshot(_root));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task DryRun_PreservesSubtaskLifecycleProseSchedulingMetadataAndOrder()
    {
        File.WriteAllText(Path.Combine(_root, "wiki", "todo", "legacy-parent.md"), $$"""
            ---
            id: legacy-parent
            title: Legacy parent
            status: todo
            priority: medium
            type: pbi
            created: 2026-08-01
            links:
            - type: ado
              value: '123'
            ---

            ## Subtasks

            ### [ ] First
            - status: todo

            ### [ ] Focus
            - status: in_progress
            - size: focus
            - due: 2026-09-10
            - my_day: true

            Focus notes.

            ### [ ] Waiting
            - status: blocked
            - blocker: Waiting for approval

            ### [x] Finished
            - status: done
            - completed: 2026-08-15

            ### [ ] Dropped
            - status: dropped
            - custom_key: exact value

            ## Notes

            ## Related
            """);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var promotions = plan.RootElement.GetProperty("promotions").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(
                new[] { 1, 2, 3, 4, 5 },
                promotions.Select(item => item.GetProperty("source_order").GetInt32()).ToArray());
            CollectionAssert.AreEqual(
                new[] { "todo", "in-progress", "blocked", "done", "cancelled" },
                promotions.Select(item => item.GetProperty("task_status").GetString()).ToArray());

            var children = plan.RootElement.GetProperty("changes").EnumerateArray()
                .Where(change => change.GetProperty("kind").GetString() == "create")
                .Select(change => Encoding.UTF8.GetString(Convert.FromBase64String(
                    change.GetProperty("updated_base64").GetString()!)))
                .ToArray();
            Assert.HasCount(5, children);
            StringAssert.Contains(children[1], "size: focus");
            StringAssert.Contains(children[1], "due: 2026-09-10");
            StringAssert.Contains(children[1], $"my_day: {DateTime.Today:yyyy-MM-dd}");
            StringAssert.Contains(children[1], "Focus notes.");
            StringAssert.Contains(children[2], "blocked_reason: Waiting for approval");
            StringAssert.Contains(children[3], "completed_at: 2026-08-15");
            StringAssert.Contains(children[4], "status: cancelled");
            StringAssert.Contains(children[4], "source_status: dropped");
            StringAssert.Contains(children[4], "custom_key: exact value");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task Execute_VerifiesChangedPathBackupAndAppliesAcceptedPlan()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var originalParent = await File.ReadAllBytesAsync(parentPath);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            var dryRun = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.StandardError);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var childId = plan.RootElement.GetProperty("promotions")[0].GetProperty("child_id").GetString()!;

            var execute = await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(0, execute.ExitCode, execute.StandardError);
            using var output = JsonDocument.Parse(execute.StandardOutput);
            Assert.AreEqual("applied", output.RootElement.GetProperty("outcome").GetString());
            var migratedParent = await File.ReadAllTextAsync(parentPath);
            StringAssert.Contains(migratedParent, "type: parent");
            StringAssert.Contains(migratedParent, "source_kind: Product Backlog Item");
            Assert.IsFalse(migratedParent.Contains("### [ ] Do the thing", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "wiki", "todo", $"{childId}.md")));
            CollectionAssert.AreEqual(
                originalParent,
                await File.ReadAllBytesAsync(Path.Combine(
                    backupPath, "originals", "wiki", "todo", "legacy-parent.md")));
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, "manifest.json")));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Validate_ProvesAppliedPlanAndRollbackBackup()
    {
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture")).ExitCode);

            var validation = await RunCli(
                "parent-migration", "validate",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath);

            Assert.AreEqual(0, validation.ExitCode, validation.StandardError);
            using var output = JsonDocument.Parse(validation.StandardOutput);
            Assert.AreEqual("valid", output.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual(0, output.RootElement.GetProperty("diagnostics").GetArrayLength());
            Assert.IsTrue(output.RootElement.GetProperty("rollback_viable").GetBoolean());
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Rollback_RestoresOriginalBytesAndRemovesCreatedChildren()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var originalParent = await File.ReadAllBytesAsync(parentPath);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var childId = plan.RootElement.GetProperty("promotions")[0].GetProperty("child_id").GetString()!;
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture")).ExitCode);

            var rollback = await RunCli(
                "parent-migration", "rollback",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(0, rollback.ExitCode, rollback.StandardError);
            using var output = JsonDocument.Parse(rollback.StandardOutput);
            Assert.AreEqual("rolled_back", output.RootElement.GetProperty("outcome").GetString());
            CollectionAssert.AreEqual(originalParent, await File.ReadAllBytesAsync(parentPath));
            Assert.IsFalse(File.Exists(Path.Combine(_root, "wiki", "todo", $"{childId}.md")));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Execute_FailureRollsBackAndRetryUsesSamePlanBackupAndChildId()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var originalParent = await File.ReadAllBytesAsync(parentPath);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var childId = plan.RootElement.GetProperty("promotions")[0].GetProperty("child_id").GetString()!;

            var failed = await RunCli(
                new Dictionary<string, string?>
                {
                    ["GLASSWORK_MIGRATION_TEST_FAIL_AFTER_WRITES"] = "1",
                },
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, failed.ExitCode);
            using var failedOutput = JsonDocument.Parse(failed.StandardOutput);
            Assert.AreEqual("failed", failedOutput.RootElement.GetProperty("outcome").GetString());
            CollectionAssert.AreEqual(originalParent, await File.ReadAllBytesAsync(parentPath));
            Assert.IsFalse(File.Exists(Path.Combine(_root, "wiki", "todo", $"{childId}.md")));

            var retry = await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(0, retry.ExitCode, retry.StandardError);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "wiki", "todo", $"{childId}.md")));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Execute_RefusesWhenAnyVaultWriterIsReported()
    {
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);

            var result = await RunCli(
                new Dictionary<string, string?>
                {
                    ["GLASSWORK_MIGRATION_TEST_ADDITIONAL_WRITERS"] = "glasswork-mcp:42",
                },
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual("vault_writers_running", output.RootElement.GetProperty("error").GetString());
            Assert.IsFalse(Directory.Exists(backupPath));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task DryRun_UsesLegacyBodyAdoIdentityForAuthoritativeSourceKind()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var content = await File.ReadAllTextAsync(parentPath);
        var linksStart = content.IndexOf("links:", StringComparison.Ordinal);
        var frontmatterEnd = content.IndexOf("---", linksStart, StringComparison.Ordinal);
        content = content.Remove(linksStart, frontmatterEnd - linksStart);
        content = content.Replace(
            "Original framing.",
            "ADO 123 - https://msazure.visualstudio.com/One/_workitems/edit/123\n\nOriginal framing.",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(parentPath, content);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            Assert.AreEqual(0, plan.RootElement.GetProperty("unresolved_source_kinds").GetArrayLength());
            var parentChange = plan.RootElement.GetProperty("changes").EnumerateArray()
                .Single(change => change.GetProperty("legacy_parent").GetBoolean());
            var updated = Encoding.UTF8.GetString(Convert.FromBase64String(
                parentChange.GetProperty("updated_base64").GetString()!));
            StringAssert.Contains(updated, "source_kind: Product Backlog Item");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task Rollback_RecoversAProcessExitWithRetainedJournal()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var originalParent = await File.ReadAllBytesAsync(parentPath);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            var interrupted = await RunCli(
                new Dictionary<string, string?>
                {
                    ["GLASSWORK_MIGRATION_TEST_EXIT_AFTER_WRITES"] = "1",
                },
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(86, interrupted.ExitCode);
            Assert.IsTrue(File.Exists(Path.Combine(
                _root, "wiki", "todo", ".glasswork", "parent-migration-journal.json")));
            CollectionAssert.AreNotEqual(originalParent, await File.ReadAllBytesAsync(parentPath));

            var rollback = await RunCli(
                "parent-migration", "rollback",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(0, rollback.ExitCode, rollback.StandardError);
            CollectionAssert.AreEqual(originalParent, await File.ReadAllBytesAsync(parentPath));
            Assert.IsFalse(File.Exists(Path.Combine(
                _root, "wiki", "todo", ".glasswork", "parent-migration-journal.json")));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Rollback_RefusesUnchangedArtifactDriftBeforeMutatingAnyTask()
    {
        var artifactDirectory = Path.Combine(_root, "wiki", "todo", "legacy-parent.artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(artifactDirectory, "plan.md");
        await File.WriteAllTextAsync(artifactPath, "original artifact");
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var childId = plan.RootElement.GetProperty("promotions")[0].GetProperty("child_id").GetString()!;
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture")).ExitCode);
            await File.WriteAllTextAsync(artifactPath, "newer external artifact");

            var rollback = await RunCli(
                "parent-migration", "rollback",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, rollback.ExitCode);
            using var output = JsonDocument.Parse(rollback.StandardOutput);
            Assert.AreEqual("rollback_failed", output.RootElement.GetProperty("error").GetString());
            StringAssert.Contains(await File.ReadAllTextAsync(parentPath), "type: parent");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "wiki", "todo", $"{childId}.md")));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task DryRun_BlocksMissingBlockerDetailsWithoutWritingVault()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var content = await File.ReadAllTextAsync(parentPath);
        content = content.Replace("- status: todo", "- status: blocked", StringComparison.Ordinal);
        await File.WriteAllTextAsync(parentPath, content);
        var before = Snapshot(_root);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            CollectionAssert.Contains(
                output.RootElement.GetProperty("blocking_diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray(),
                "blocked_subtask_missing_details");
            CollectionAssert.AreEquivalent(before, Snapshot(_root));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task Execute_RefusesReadBasisDriftBeforeCreatingBackup()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            await File.AppendAllTextAsync(parentPath, "\nexternal edit\n");

            var result = await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual("read_basis_drift", output.RootElement.GetProperty("error").GetString());
            Assert.IsFalse(Directory.Exists(backupPath));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task DryRun_CanonicalizesResolvableParentAndFingerprintsArtifacts()
    {
        var childPath = Path.Combine(_root, "wiki", "todo", "existing-child.md");
        await File.WriteAllTextAsync(childPath, """
            ---
            id: existing-child
            title: Existing child
            status: todo
            priority: medium
            created: 2026-08-01
            parent: '123'
            ---

            ## Subtasks

            ## Notes

            ## Related
            """);
        var artifacts = Path.Combine(_root, "wiki", "todo", "legacy-parent.artifacts");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "evidence.md"), "unchanged");
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var childChange = plan.RootElement.GetProperty("changes").EnumerateArray()
                .Single(change => change.GetProperty("relative_path").GetString() == "wiki/todo/existing-child.md");
            var updated = Encoding.UTF8.GetString(Convert.FromBase64String(
                childChange.GetProperty("updated_base64").GetString()!));
            StringAssert.Contains(updated, "parent: legacy-parent");
            CollectionAssert.Contains(
                plan.RootElement.GetProperty("read_basis").EnumerateArray()
                    .Select(item => item.GetProperty("relative_path").GetString()).ToArray(),
                "wiki/todo/legacy-parent.artifacts/evidence.md");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task DryRun_ReportsMissingSourceKindWithoutInventingOne()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "ado-evidence.json"), "[]");
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            CollectionAssert.AreEqual(
                new[] { "legacy-parent" },
                plan.RootElement.GetProperty("unresolved_source_kinds").EnumerateArray()
                    .Select(item => item.GetString()).ToArray());
            var parentChange = plan.RootElement.GetProperty("changes").EnumerateArray()
                .Single(change => change.GetProperty("legacy_parent").GetBoolean());
            var updated = Encoding.UTF8.GetString(Convert.FromBase64String(
                parentChange.GetProperty("updated_base64").GetString()!));
            Assert.IsFalse(updated.Contains("source_kind:", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task DryRun_BlocksCaseInsensitiveTaskIdCollisionsAcrossLegacyDoneFolder()
    {
        var done = Path.Combine(_root, "wiki", "todo", "done");
        Directory.CreateDirectory(done);
        await File.WriteAllTextAsync(Path.Combine(done, "other.md"), """
            ---
            id: LEGACY-PARENT
            title: Colliding legacy task
            status: done
            priority: medium
            created: 2026-08-01
            ---

            ## Subtasks

            ## Notes

            ## Related
            """);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            CollectionAssert.Contains(
                output.RootElement.GetProperty("blocking_diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray(),
                "duplicate_task_id");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task DryRun_BlocksAdoEvidenceWithoutValidRetrievalTime()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "ado-evidence.json"), """
            [
              {
                "task_id": "legacy-parent",
                "ado_id": 123,
                "source_kind": "Product Backlog Item",
                "retrieved_at": "not-a-timestamp"
              }
            ]
            """);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            CollectionAssert.Contains(
                output.RootElement.GetProperty("blocking_diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray(),
                "invalid_ado_evidence");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task Rollback_RefusesJournalFromDifferentPlanBeforeMutating()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture")).ExitCode);
            var journalPath = Path.Combine(
                _root, "wiki", "todo", ".glasswork", "parent-migration-journal.json");
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            await File.WriteAllTextAsync(journalPath, """
                {
                  "schema_version": 1,
                  "operation_id": "other-operation",
                  "plan_hash": "other-plan",
                  "backup_path": "C:\\other",
                  "committed": false,
                  "entries": []
                }
                """);

            var rollback = await RunCli(
                "parent-migration", "rollback",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, rollback.ExitCode);
            StringAssert.Contains(await File.ReadAllTextAsync(parentPath), "type: parent");
            Assert.IsTrue(File.Exists(journalPath));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task Execute_RefusesTargetDriftImmediatelyBeforeFirstWrite()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);

            var result = await RunCli(
                new Dictionary<string, string?>
                {
                    ["GLASSWORK_MIGRATION_TEST_MUTATE_BEFORE_WRITE"] = "wiki/todo/legacy-parent.md",
                },
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, result.ExitCode);
            StringAssert.Contains(await File.ReadAllTextAsync(parentPath), "fixture concurrent edit");
            Assert.IsTrue(File.Exists(Path.Combine(
                _root, "wiki", "todo", ".glasswork", "parent-migration-journal.json")));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task DryRun_BlocksUnknownSubtaskStatusWithoutNormalizingItAway()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var content = await File.ReadAllTextAsync(parentPath);
        content = content.Replace("- status: todo", "- status: waiting", StringComparison.Ordinal);
        await File.WriteAllTextAsync(parentPath, content);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            CollectionAssert.Contains(
                output.RootElement.GetProperty("blocking_diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray(),
                "unsupported_subtask_status");
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            Assert.AreEqual(
                "waiting",
                plan.RootElement.GetProperty("promotions")[0].GetProperty("source_status").GetString());
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task Rollback_RechecksCreatedChildImmediatelyBeforeDelete()
    {
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var childId = plan.RootElement.GetProperty("promotions")[0].GetProperty("child_id").GetString()!;
            var childRelativePath = $"wiki/todo/{childId}.md";
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture")).ExitCode);

            var rollback = await RunCli(
                new Dictionary<string, string?>
                {
                    ["GLASSWORK_MIGRATION_TEST_MUTATE_BEFORE_ROLLBACK_WRITE"] = childRelativePath,
                },
                "parent-migration", "rollback",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, rollback.ExitCode);
            var childPath = Path.Combine(_root, childRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(childPath));
            StringAssert.Contains(await File.ReadAllTextAsync(childPath), "fixture concurrent edit");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task DryRun_BlocksMultipleDistinctCanonicalAdoLinks()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var content = await File.ReadAllTextAsync(parentPath);
        content = content.Replace(
            "  value: '123'",
            "  value: '123'\n- type: ado\n  value: '456'",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(parentPath, content);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            CollectionAssert.Contains(
                output.RootElement.GetProperty("blocking_diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray(),
                "ambiguous_ado_identity");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task Execute_RefusesNewScopedFileAddedAfterDryRun()
    {
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            await File.WriteAllTextAsync(Path.Combine(_root, "wiki", "todo", "new-task.md"), """
                ---
                id: new-task
                title: New task
                status: todo
                priority: medium
                created: 2026-08-31
                ---

                ## Subtasks

                ## Notes

                ## Related
                """);

            var execute = await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, execute.ExitCode);
            using var output = JsonDocument.Parse(execute.StandardOutput);
            Assert.AreEqual("read_basis_drift", output.RootElement.GetProperty("error").GetString());
            Assert.IsFalse(Directory.Exists(backupPath));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    [TestMethod]
    public async Task DryRun_DoesNotTreatBodyTextAsLegacyTaskType()
    {
        var ordinaryPath = Path.Combine(_root, "wiki", "todo", "ordinary.md");
        await File.WriteAllTextAsync(ordinaryPath, """
            ---
            id: ordinary
            title: Ordinary task
            status: todo
            priority: medium
            created: 2026-08-31
            ---

            Example configuration:

            type: pbi

            ## Subtasks

            ### [ ] Keep inline

            ## Notes

            ## Related
            """);
        var original = await File.ReadAllBytesAsync(ordinaryPath);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var output = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual(1, output.RootElement.GetProperty("converted_parent_count").GetInt32());
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            Assert.IsFalse(plan.RootElement.GetProperty("changes").EnumerateArray()
                .Any(change => change.GetProperty("relative_path").GetString() == "wiki/todo/ordinary.md"));
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(ordinaryPath));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task DryRun_BlocksAmbiguousAdoEvenWhenSourceKindAlreadyExists()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var content = await File.ReadAllTextAsync(parentPath);
        content = content.Replace(
            "type: pbi",
            "type: pbi\nsource_kind: Product Backlog Item",
            StringComparison.Ordinal);
        content = content.Replace(
            "  value: '123'",
            "  value: '123'\n- type: ado\n  value: '456'",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(parentPath, content);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            CollectionAssert.Contains(
                output.RootElement.GetProperty("blocking_diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray(),
                "ambiguous_ado_identity");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task DryRun_BlocksContradictoryAdoEvidenceWhenSourceKindAlreadyExists()
    {
        var parentPath = Path.Combine(_root, "wiki", "todo", "legacy-parent.md");
        var content = await File.ReadAllTextAsync(parentPath);
        content = content.Replace(
            "type: pbi",
            "type: pbi\nsource_kind: Product Backlog Item",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(parentPath, content);
        await File.WriteAllTextAsync(Path.Combine(_root, "ado-evidence.json"), """
            [
              {
                "task_id": "legacy-parent",
                "ado_id": 456,
                "source_kind": "Feature",
                "retrieved_at": "2026-08-31T16:00:00Z"
              }
            ]
            """);
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");

        try
        {
            var result = await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath);

            Assert.AreEqual(1, result.ExitCode);
            using var output = JsonDocument.Parse(result.StandardOutput);
            CollectionAssert.Contains(
                output.RootElement.GetProperty("blocking_diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray(),
                "ado_evidence_mismatch");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

    [TestMethod]
    public async Task Rollback_RejectsIncompleteBackupManifestBeforeRemovingChildren()
    {
        var planPath = Path.Combine(Path.GetTempPath(), $"glasswork-plan-{Guid.NewGuid():N}.json");
        var backupPath = Path.Combine(Path.GetTempPath(), $"glasswork-backup-{Guid.NewGuid():N}");

        try
        {
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "dry-run",
                "--vault", _root,
                "--ado-evidence", Path.Combine(_root, "ado-evidence.json"),
                "--plan", planPath)).ExitCode);
            using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath));
            var childId = plan.RootElement.GetProperty("promotions")[0].GetProperty("child_id").GetString()!;
            var childPath = Path.Combine(_root, "wiki", "todo", $"{childId}.md");
            Assert.AreEqual(0, (await RunCli(
                "parent-migration", "execute",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture")).ExitCode);

            var manifestPath = Path.Combine(backupPath, "manifest.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var entries = manifest["entries"]!.AsArray();
            var original = entries.Single(node => node!["kind"]!.GetValue<string>() == "original");
            entries.Remove(original);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

            var rollback = await RunCli(
                "parent-migration", "rollback",
                "--vault", _root,
                "--plan", planPath,
                "--backup", backupPath,
                "--fixture");

            Assert.AreEqual(1, rollback.ExitCode);
            Assert.IsTrue(File.Exists(childPath));
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(_root, "wiki", "todo", "legacy-parent.md")),
                "type: parent");
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, recursive: true);
        }
    }

    private static async Task<CliResult> RunCli(params string[] arguments)
        => await RunCli(new Dictionary<string, string?>(), arguments);

    private static async Task<CliResult> RunCli(
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "glasswork-maintenance.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var pair in environment)
            start.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, standardOutput, standardError);
    }

    private static string[] Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                return $"{relative}:{hash}";
            })
            .ToArray();

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
