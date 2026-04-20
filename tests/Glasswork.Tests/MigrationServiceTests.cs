using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class MigrationServiceTests
{
    private readonly MigrationService _migration = new();
    private readonly FrontmatterParser _parser = new();

    [TestMethod]
    public void IsV1Format_BodyWithoutSubtasksHeader_ReturnsTrue()
    {
        var content = """
            ---
            id: legacy
            title: Legacy task
            ---

            Just a plain body, no subtasks heading.
            """;

        Assert.IsTrue(MigrationService.IsV1Format(content));
    }

    [TestMethod]
    public void IsV1Format_BodyWithSubtasksHeader_ReturnsFalse()
    {
        var content = """
            ---
            id: modern
            title: Modern task
            ---

            ## Subtasks

            ### [ ] One
            """;

        Assert.IsFalse(MigrationService.IsV1Format(content));
    }

    [TestMethod]
    public void IsV1Format_EmptyBodyNoSubtasks_ReturnsTrue()
    {
        var content = """
            ---
            id: empty
            title: Empty
            ---
            """;

        Assert.IsTrue(MigrationService.IsV1Format(content));
    }

    [TestMethod]
    public void Parse_V1File_SetsIsV1FormatTrue()
    {
        var markdown = """
            ---
            id: legacy
            title: Legacy
            ---

            A V1 body.
            """;

        var task = _parser.Parse(markdown);
        Assert.IsTrue(task.IsV1Format);
    }

    [TestMethod]
    public void Parse_V2File_SetsIsV1FormatFalse()
    {
        var markdown = """
            ---
            id: modern
            title: Modern
            ---

            Body.

            ## Subtasks

            ### [ ] one
            """;

        var task = _parser.Parse(markdown);
        Assert.IsFalse(task.IsV1Format);
    }

    [TestMethod]
    public void MigrateToV2_V1File_AppendsCanonicalSectionsInOrder()
    {
        var v1 = """
            ---
            id: legacy
            title: Legacy
            ---

            Original body content.
            """;

        var migrated = _migration.MigrateToV2(v1);

        Assert.IsTrue(migrated.Contains("Original body content."), "body must be preserved");
        Assert.IsTrue(migrated.Contains("## Subtasks"), "must have Subtasks section");
        Assert.IsTrue(migrated.Contains("## Notes"), "must have Notes section");
        Assert.IsTrue(migrated.Contains("## Related"), "must have Related section");

        var subIdx = migrated.IndexOf("## Subtasks", StringComparison.Ordinal);
        var notesIdx = migrated.IndexOf("## Notes", StringComparison.Ordinal);
        var relIdx = migrated.IndexOf("## Related", StringComparison.Ordinal);
        Assert.IsTrue(subIdx < notesIdx, "Subtasks before Notes");
        Assert.IsTrue(notesIdx < relIdx, "Notes before Related");
    }

    [TestMethod]
    public void MigrateToV2_IsIdempotent()
    {
        var v1 = """
            ---
            id: legacy
            title: Legacy
            ---

            Body line.
            """;

        var once = _migration.MigrateToV2(v1);
        var twice = _migration.MigrateToV2(once);

        Assert.AreEqual(once, twice, "running migration twice yields the same content");
    }

    [TestMethod]
    public void MigrateToV2_PreservesFrontmatterByteForByte()
    {
        var v1 = """
            ---
            id: legacy
            title: Has frontmatter quirks
            status: in-progress
            priority: high
            tags:
              - alpha
              - beta
            ---

            Body.
            """;

        var migrated = _migration.MigrateToV2(v1);

        Assert.IsTrue(migrated.Contains("id: legacy"));
        Assert.IsTrue(migrated.Contains("title: Has frontmatter quirks"));
        Assert.IsTrue(migrated.Contains("status: in-progress"));
        Assert.IsTrue(migrated.Contains("priority: high"));
        Assert.IsTrue(migrated.Contains("- alpha"));
        Assert.IsTrue(migrated.Contains("- beta"));
    }

    [TestMethod]
    public void MigrateToV2_PreservesBodyContent()
    {
        var v1 = """
            ---
            id: legacy
            title: Legacy
            ---

            Line 1.
            Line 2.

            A blank-line-separated paragraph.
            """;

        var migrated = _migration.MigrateToV2(v1);

        Assert.IsTrue(migrated.Contains("Line 1."));
        Assert.IsTrue(migrated.Contains("Line 2."));
        Assert.IsTrue(migrated.Contains("A blank-line-separated paragraph."));
    }

    [TestMethod]
    public void MigrateToV2_AlreadyV2_ReturnsUnchanged()
    {
        var v2 = """
            ---
            id: modern
            title: Modern
            ---

            Body.

            ## Subtasks

            ### [ ] one

            ## Notes

            ## Related
            """;

        var migrated = _migration.MigrateToV2(v2);
        Assert.AreEqual(v2, migrated);
    }

    [TestMethod]
    public void MigrateToV2_PartialV2_AddsOnlyMissingSections()
    {
        // Has Subtasks but missing Notes/Related — migration must add only what's missing.
        var partial = """
            ---
            id: partial
            title: Partial
            ---

            Body.

            ## Subtasks

            ### [ ] one
            """;

        var migrated = _migration.MigrateToV2(partial);

        Assert.IsTrue(migrated.Contains("## Subtasks"));
        Assert.IsTrue(migrated.Contains("### [ ] one"), "existing subtask preserved");
        Assert.IsTrue(migrated.Contains("## Notes"));
        Assert.IsTrue(migrated.Contains("## Related"));

        // Idempotent on partial too
        var twice = _migration.MigrateToV2(migrated);
        Assert.AreEqual(migrated, twice);
    }

    [TestMethod]
    public void MigrateToV2_AfterMigration_FileParsesAsV2()
    {
        var v1 = """
            ---
            id: legacy
            title: Legacy
            ---

            Body.
            """;

        var migrated = _migration.MigrateToV2(v1);
        var task = _parser.Parse(migrated);

        Assert.IsFalse(task.IsV1Format, "after migration the task is no longer V1");
    }

    [TestMethod]
    public void MigrateToV2_MissingFrontmatter_Throws()
    {
        Assert.ThrowsExactly<FormatException>(() => _migration.MigrateToV2("no frontmatter here"));
    }

    // ===== V2-as-default and bulk migration =====

    [TestMethod]
    public void Serialize_NewEmptyTask_EmitsAllCanonicalSections()
    {
        var task = new Glasswork.Core.Models.GlassworkTask
        {
            Id = "fresh",
            Title = "Fresh task",
            Status = "todo",
            Priority = "medium",
            Created = new DateTime(2026, 1, 1),
        };

        var markdown = _parser.Serialize(task);

        Assert.IsTrue(markdown.Contains("## Subtasks"), "## Subtasks must appear even when no subtasks");
        Assert.IsTrue(markdown.Contains("## Notes"), "## Notes must appear even when body is empty");
        Assert.IsTrue(markdown.Contains("## Related"), "## Related must appear even when no related links");
    }

    [TestMethod]
    public void Serialize_NewEmptyTask_IsNotV1Format()
    {
        var task = new Glasswork.Core.Models.GlassworkTask
        {
            Id = "fresh",
            Title = "Fresh task",
            Status = "todo",
            Priority = "medium",
            Created = new DateTime(2026, 1, 1),
        };

        var markdown = _parser.Serialize(task);
        Assert.IsFalse(MigrationService.IsV1Format(markdown), "newly-serialized tasks must be V2 from birth");
        Assert.IsFalse(_parser.Parse(markdown).IsV1Format, "parsed task must not be flagged V1");
    }

    [TestMethod]
    public void Serialize_CanonicalSections_AppearInOrder()
    {
        var task = new Glasswork.Core.Models.GlassworkTask
        {
            Id = "ordered",
            Title = "Ordered",
            Status = "todo",
            Priority = "medium",
            Created = new DateTime(2026, 1, 1),
        };

        var markdown = _parser.Serialize(task);
        var subIdx = markdown.IndexOf("## Subtasks", StringComparison.Ordinal);
        var notesIdx = markdown.IndexOf("## Notes", StringComparison.Ordinal);
        var relIdx = markdown.IndexOf("## Related", StringComparison.Ordinal);

        Assert.IsTrue(subIdx > 0, "Subtasks present");
        Assert.IsTrue(notesIdx > subIdx, "Notes after Subtasks");
        Assert.IsTrue(relIdx > notesIdx, "Related after Notes");
    }
}
