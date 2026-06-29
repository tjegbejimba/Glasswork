using Glasswork.Core.Services;

namespace Glasswork.Tests;

[TestClass]
public class TaskTypeBackfillServiceTests
{
    // ----- StampType -----

    [TestMethod]
    public void StampType_LegacyAdoLinkFile_InsertsTypeAfterPriority_PreservingEverythingElse()
    {
        var content = """
            ---
            id: general-arm-manifests-improvements
            title: 'General ARM Manifests Improvements'
            status: in-progress
            priority: medium
            created: 2026-05-18
            due: 2026-07-11
            ado_link: 14480984
            parent: 6417195
            ---

            ADO 14480984 — https://msazure.visualstudio.com/One/_workitems/edit/14480984

            ## Subtasks
            """.ReplaceLineEndings("\n");

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsTrue(changed);
        StringAssert.Contains(result, "priority: medium\ntype: pbi\ncreated: 2026-05-18");
        // Legacy ado_link survives — no serializer round-trip / no churn (ADR 0016).
        StringAssert.Contains(result, "ado_link: 14480984");
        StringAssert.Contains(result, "parent: 6417195");
        StringAssert.Contains(result, "## Subtasks");
    }

    [TestMethod]
    public void StampType_FileAlreadyHasTopLevelType_IsNoOp()
    {
        var content = """
            ---
            id: x
            title: X
            status: todo
            priority: medium
            type: pbi
            ---

            ## Subtasks
            """.ReplaceLineEndings("\n");

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsFalse(changed);
        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void StampType_NestedLinksTypeEntry_NotTreatedAsTaskType_StillStamps()
    {
        // A `links:` array entry has a nested `type:` key that must NOT be mistaken for the
        // top-level task type — otherwise the file would be wrongly skipped as "already typed".
        var content = """
            ---
            id: x
            title: X
            status: todo
            priority: medium
            links:
            - type: ado
              value: '123'
            ---

            ## Subtasks
            """.ReplaceLineEndings("\n");

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsTrue(changed);
        StringAssert.Contains(result, "priority: medium\ntype: pbi\nlinks:");
        StringAssert.Contains(result, "- type: ado");
    }

    [TestMethod]
    public void StampType_TypeColonInBody_IsIgnored_StampsFrontmatterOnly()
    {
        var content = """
            ---
            id: x
            title: X
            status: todo
            priority: medium
            ---

            Some notes mentioning type: weird in prose.

            ## Subtasks
            """.ReplaceLineEndings("\n");

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsTrue(changed);
        StringAssert.Contains(result, "priority: medium\ntype: pbi\n");
        StringAssert.Contains(result, "type: weird in prose.");
    }

    [TestMethod]
    public void StampType_CrlfFile_PreservesCrlfNewlines()
    {
        var content = "---\r\nid: x\r\ntitle: X\r\nstatus: todo\r\npriority: medium\r\n---\r\n\r\n## Subtasks\r\n";

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsTrue(changed);
        StringAssert.Contains(result, "priority: medium\r\ntype: pbi\r\n---");
        // Every LF is preceded by a CR — no lone LF was introduced.
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(result, "(?<!\r)\n"));
    }

    [TestMethod]
    public void StampType_NoTrailingNewline_Preserved()
    {
        var content = "---\nid: x\npriority: medium\n---\n\nbody";

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsTrue(changed);
        Assert.IsFalse(result.EndsWith("\n"), "trailing-newline state must be preserved");
        StringAssert.EndsWith(result, "body");
    }

    [TestMethod]
    public void StampType_NoPriorityLine_InsertsAfterStatus()
    {
        var content = """
            ---
            id: x
            title: X
            status: todo
            created: 2026-01-01
            ---

            ## Subtasks
            """.ReplaceLineEndings("\n");

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsTrue(changed);
        StringAssert.Contains(result, "status: todo\ntype: pbi\ncreated: 2026-01-01");
    }

    [TestMethod]
    public void StampType_NoPriorityNoStatus_InsertsBeforeClosingDelimiter()
    {
        var content = """
            ---
            id: x
            title: X
            created: 2026-01-01
            ---

            body
            """.ReplaceLineEndings("\n");

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsTrue(changed);
        StringAssert.Contains(result, "created: 2026-01-01\ntype: pbi\n---");
    }

    [TestMethod]
    public void StampType_TaskType_IsNoOp()
    {
        var content = "---\nid: x\npriority: medium\n---\n\nbody\n";

        var (result, changed) = TaskTypeBackfillService.StampType(content, "task");

        Assert.IsFalse(changed);
        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void StampType_NoFrontmatter_IsNoOp()
    {
        var content = "Just a plain file with no frontmatter.\n";

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsFalse(changed);
        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void StampType_UnterminatedFrontmatter_IsNoOp()
    {
        var content = "---\nid: x\npriority: medium\n\nno closing delimiter\n";

        var (result, changed) = TaskTypeBackfillService.StampType(content, "pbi");

        Assert.IsFalse(changed);
        Assert.AreEqual(content, result);
    }

    // ----- ResolveAdoId -----

    [TestMethod]
    public void ResolveAdoId_TopLevelAdoLinkFrontmatter_Resolves()
    {
        var content = """
            ---
            id: x
            ado_link: 14480984
            ---

            ADO 14480984 — https://msazure.visualstudio.com/One/_workitems/edit/14480984
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.Resolved, r.Status);
        Assert.AreEqual(14480984, r.Id);
    }

    [TestMethod]
    public void ResolveAdoId_BodyAdoMarkerOnly_Resolves()
    {
        var content = """
            ---
            id: x
            ---

            ADO 36083004 — https://msazure.visualstudio.com/One/_workitems/edit/36083004
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.Resolved, r.Status);
        Assert.AreEqual(36083004, r.Id);
    }

    [TestMethod]
    public void ResolveAdoId_WorkitemUrlOnly_Resolves()
    {
        var content = """
            ---
            id: x
            ---

            See https://msazure.visualstudio.com/One/_workitems/edit/37569824 for details.
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.Resolved, r.Status);
        Assert.AreEqual(37569824, r.Id);
    }

    [TestMethod]
    public void ResolveAdoId_NoAdoReference_ReturnsNone()
    {
        var content = """
            ---
            id: x
            ---

            A purely local task with no ADO link.
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.None, r.Status);
        Assert.IsNull(r.Id);
    }

    [TestMethod]
    public void ResolveAdoId_AdoLinkWins_OverDifferentBodyMarker()
    {
        // ado_link is the canonical frontmatter field; it takes precedence over a body
        // marker that references a different (e.g. cross-referenced) work item.
        var content = """
            ---
            id: x
            ado_link: 14480984
            ---

            ADO 99999999 — cross-reference only
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.Resolved, r.Status);
        Assert.AreEqual(14480984, r.Id);
    }

    [TestMethod]
    public void ResolveAdoId_MultipleDistinctBodyMarkers_IsAmbiguous()
    {
        var content = """
            ---
            id: x
            ---

            ADO 14480984 — one
            ADO 36083004 — another
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.Ambiguous, r.Status);
    }

    [TestMethod]
    public void ResolveAdoId_MidLineCasualMention_IsNotMatchedByBodyMarker()
    {
        // The line-anchored ^ADO <id> marker must not pick up casual prose mentions.
        var content = """
            ---
            id: x
            ---

            Same shape as ADO 37076384, but this task has no ADO link of its own.
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.None, r.Status);
    }

    [TestMethod]
    public void ResolveAdoId_FencedAdoLinkInBody_IsIgnored_BodyMarkerWins()
    {
        // A fenced/quoted `ado_link:` in the BODY must never be treated as the frontmatter
        // field. ado_link is resolved only within the frontmatter span; here the real id
        // comes from the body marker + URL (which agree).
        var content = """
            ---
            id: x
            status: todo
            priority: medium
            ---

            Pasted from another task for reference:

            ```
            ado_link: 11111111
            ```

            ADO 22222222 — https://msazure.visualstudio.com/One/_workitems/edit/22222222
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.Resolved, r.Status);
        Assert.AreEqual(22222222, r.Id);
    }

    [TestMethod]
    public void ResolveAdoId_FencedBodyMarkerAndDifferentUrl_IsAmbiguous()
    {
        // A fenced ^ADO marker (one id) plus a real work-item URL (a different id) must be
        // reported Ambiguous — not first-match-wins on the fenced marker.
        var content = """
            ---
            id: x
            ---

            Pasted log:

            ```
            ADO 11111111 — stale reference
            ```

            Real link: https://msazure.visualstudio.com/One/_workitems/edit/22222222
            """.ReplaceLineEndings("\n");

        var r = TaskTypeBackfillService.ResolveAdoId(content);

        Assert.AreEqual(AdoIdStatus.Ambiguous, r.Status);
    }
}
