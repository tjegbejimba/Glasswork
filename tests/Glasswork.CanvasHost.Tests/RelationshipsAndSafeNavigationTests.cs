using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Black-box coverage for issue #558: the canvas detail must match native
/// Task Detail's relationship surface (Links, Related, direct Children,
/// Backlinks, blocker/parent/ADO metadata) and expose only safe viewer
/// navigation — activating a Task reference loads it into the Session Task
/// Set, and every other reference routes through the existing Obsidian /
/// ArtifactLinkPolicy allowlist rather than a bespoke canvas-only path.
/// </summary>
[TestClass]
public sealed class RelationshipsAndSafeNavigationTests : CanvasHostTestBase
{
    /// <summary>
    /// Every test in this class starts an isolated host with an empty,
    /// per-test UI state file so ambient state on the machine running the
    /// tests (e.g. a real configured ADO base URL) can never leak into the
    /// ADO link-resolution assertions below.
    /// </summary>
    private static string CreateEmptyUiStatePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"canvas-relationships-ui-state-{Guid.NewGuid()}.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static void AddRichTask(string vault)
    {
        var todo = Path.Combine(vault, "wiki", "todo");
        File.WriteAllText(Path.Combine(todo, "rich.md"), """
        ---
        id: rich
        title: Rich task
        status: todo
        priority: high
        type: pbi
        created: 2026-09-01
        parent: demo
        links:
          - type: ado
            value: 4321
            label: Sample ADO
          - type: other
            value: not-a-url
        ---

        Rich description.

        ## Related

        - [[concepts/task-linking|Task linking]]
        - [[concepts/ghost-page]]
        """);
    }

    private static void AddBlockedTask(string vault)
    {
        var todo = Path.Combine(vault, "wiki", "todo");
        File.WriteAllText(Path.Combine(todo, "blocked-valid.md"), """
        ---
        id: blocked-valid
        title: Blocked with valid metadata
        status: blocked
        priority: medium
        type: task
        created: 2026-09-01
        blocked_reason: Waiting on design review
        blocked_at: 2026-09-01T10:00:00.0000000Z
        blocked_from_status: todo
        ---

        Blocked task description.
        """);
    }

    private static void AddChildOfDemo(string vault)
    {
        var todo = Path.Combine(vault, "wiki", "todo");
        File.WriteAllText(Path.Combine(todo, "child-of-demo.md"), """
        ---
        id: child-of-demo
        title: Child of demo
        status: todo
        priority: medium
        type: task
        created: 2026-09-01
        parent: demo
        ---

        Child description.
        """);
    }

    private static void AddConceptPageLinkingToRich(string vault)
    {
        var concepts = Path.Combine(vault, "wiki", "concepts");
        Directory.CreateDirectory(concepts);
        File.WriteAllText(Path.Combine(concepts, "task-linking.md"), """
        ---
        title: Task linking
        type: concept
        created: 2026-08-01
        ---

        See [[rich]] for context.
        """);
    }

    [TestMethod]
    public async Task Projection_ExposesStructuredLinksWithResolvedNavigationUrls()
    {
        var vault = CreateVault();
        AddRichTask(vault);
        await using var host = await StartHost(vault, "session-links", "credential-links", CreateEmptyUiStatePath());
        using var client = AuthorizedClient("credential-links");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "rich" } }));

        var response = await client.GetAsync($"{host.Url}/canvas-state");
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var links = body.RootElement.GetProperty("selectedDetail").GetProperty("projection").GetProperty("links");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, links.GetArrayLength());
        var ado = links.EnumerateArray().Single(l => l.GetProperty("typeBadgeText").GetString() == "ADO");
        Assert.AreEqual("Sample ADO", ado.GetProperty("displayText").GetString());
        Assert.IsNull(ado.GetProperty("resolvedUrl").GetString(), "an ADO link with no configured ADO base URL cannot resolve to a navigable URL");
        var other = links.EnumerateArray().Single(l => l.GetProperty("typeBadgeText").GetString() == "OTHER");
        Assert.IsNull(other.GetProperty("resolvedUrl").GetString(), "a malformed 'other' link value must not resolve to a clickable URL");
    }

    [TestMethod]
    public async Task Projection_ExposesRelatedEntriesMatchingNativeResolvedAndMissingStates()
    {
        var vault = CreateVault();
        AddRichTask(vault);
        AddConceptPageLinkingToRich(vault);
        await using var host = await StartHost(vault, "session-related", "credential-related", CreateEmptyUiStatePath());
        using var client = AuthorizedClient("credential-related");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "rich" } }));

        var response = await client.GetAsync($"{host.Url}/canvas-state");
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var related = body.RootElement.GetProperty("selectedDetail").GetProperty("projection").GetProperty("relatedEntries");

        Assert.AreEqual(2, related.GetArrayLength());
        var resolved = related.EnumerateArray().Single(r => !r.GetProperty("isMissing").GetBoolean());
        Assert.AreEqual("Task linking", resolved.GetProperty("title").GetString());
        Assert.AreEqual("wiki/concepts/task-linking.md", resolved.GetProperty("vaultPath").GetString());
        var missing = related.EnumerateArray().Single(r => r.GetProperty("isMissing").GetBoolean());
        Assert.AreEqual("wiki/concepts/ghost-page.md", missing.GetProperty("vaultPath").GetString());
    }

    [TestMethod]
    public async Task Projection_ExposesDirectChildrenAndParentTaskReference()
    {
        var vault = CreateVault();
        AddChildOfDemo(vault);
        await using var host = await StartHost(vault, "session-children", "credential-children", CreateEmptyUiStatePath());
        using var client = AuthorizedClient("credential-children");

        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));
        var response = await client.GetAsync($"{host.Url}/canvas-state");
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var children = body.RootElement.GetProperty("selectedDetail").GetProperty("projection").GetProperty("directChildren");

        Assert.AreEqual(1, children.GetArrayLength(), "the shared projection's direct-Children rule must surface a Task whose parent field matches");
        Assert.AreEqual("child-of-demo", children[0].GetProperty("id").GetString());
        Assert.AreEqual("Child of demo", children[0].GetProperty("title").GetString());

        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "child-of-demo" } }));
        var childResponse = await client.GetAsync($"{host.Url}/canvas-state");
        using var childBody = (await ReadJsonResponseAsync(childResponse)).Body;
        var childProjection = childBody.RootElement.GetProperty("selectedDetail").GetProperty("projection");
        Assert.IsTrue(childProjection.GetProperty("showParent").GetBoolean());
        Assert.AreEqual("demo", childProjection.GetProperty("parent").GetString());
        Assert.IsTrue(childProjection.GetProperty("parentIsTask").GetBoolean(), "a Parent value that resolves to a known Task must be flagged for safe in-canvas navigation");
    }

    [TestMethod]
    public async Task Projection_ExposesBacklinksFromNonTaskWikiPages()
    {
        var vault = CreateVault();
        AddRichTask(vault);
        AddConceptPageLinkingToRich(vault);
        await using var host = await StartHost(vault, "session-backlinks", "credential-backlinks", CreateEmptyUiStatePath());
        using var client = AuthorizedClient("credential-backlinks");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "rich" } }));

        var response = await client.GetAsync($"{host.Url}/canvas-state");
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var backlinks = body.RootElement.GetProperty("selectedDetail").GetProperty("projection").GetProperty("backlinks");

        Assert.AreEqual(1, backlinks.GetArrayLength());
        Assert.AreEqual("Task linking", backlinks[0].GetProperty("title").GetString());
        Assert.AreEqual("concept", backlinks[0].GetProperty("typeLabel").GetString());
    }

    [TestMethod]
    public async Task Projection_ExposesBlockedMetadataParentAndSafeActionFields()
    {
        var vault = CreateVault();
        AddBlockedTask(vault);
        await using var host = await StartHost(vault, "session-blocked", "credential-blocked", CreateEmptyUiStatePath());
        using var client = AuthorizedClient("credential-blocked");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "blocked-valid" } }));

        var response = await client.GetAsync($"{host.Url}/canvas-state");
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var projection = body.RootElement.GetProperty("selectedDetail").GetProperty("projection");

        Assert.AreEqual("Blocked: Waiting on design review", projection.GetProperty("blockedStatusText").GetString());
        Assert.AreEqual("glasswork://task/blocked-valid", projection.GetProperty("taskDeepLink").GetString(), "Copy Task link / Open in Glasswork must use the canonical glasswork:// deep-link");
        Assert.AreEqual("wiki/todo/blocked-valid.md", projection.GetProperty("taskObsidianPath").GetString());
    }

    [TestMethod]
    public async Task Canvas_RendersRelationshipSectionsAndSafeActionsInHtml()
    {
        var vault = CreateVault();
        AddRichTask(vault);
        AddChildOfDemo(vault);
        AddConceptPageLinkingToRich(vault);
        await using var host = await StartHost(vault, "session-canvas-relationships", "credential-canvas-relationships", CreateEmptyUiStatePath());
        using var client = AuthorizedClient("credential-canvas-relationships");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=rich");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "Open in Glasswork");
        StringAssert.Contains(html, "Open in Obsidian");
        StringAssert.Contains(html, "Copy Task ID");
        StringAssert.Contains(html, "Copy Task link");
        StringAssert.Contains(html, "Read-only view", "a clear read-only indicator must explain the surface boundary");
        StringAssert.Contains(html, "\"Links\"");
        StringAssert.Contains(html, "\"Related\"");
        StringAssert.Contains(html, "btn.dataset.vaultPath", "Related/Backlinks must route through the existing Wiki-page allowlist, not a bespoke canvas path");
        StringAssert.Contains(html, "\"wiki/concepts/task-linking.md\"", "the embedded payload must carry the resolved Related vault path for the client to act on");
        StringAssert.Contains(html, "\"taskObsidianPath\":\"wiki/todo/rich.md\"", "the embedded payload must carry the Task's own Obsidian vault path");

        var demoResponse = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var demoHtml = await demoResponse.Content.ReadAsStringAsync();
        StringAssert.Contains(demoHtml, "\"id\":\"child-of-demo\"", "the embedded payload must carry the direct Child so the static task-load click wiring can activate it");
        StringAssert.Contains(demoHtml, "btn.dataset.taskId=child.id", "a direct Child row must be wired to explicit Task-load navigation");
    }

    [TestMethod]
    public async Task Tasks_ActivatingAChildTaskReferenceLoadsSelectsAndMovesItWithoutDuplication()
    {
        var vault = CreateVault();
        AddChildOfDemo(vault);
        await using var host = await StartHost(vault, "session-activate-child", "credential-activate-child", CreateEmptyUiStatePath());
        using var client = AuthorizedClient("credential-activate-child");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));

        // Simulates clicking the rendered data-task-id row for the direct Child.
        var response = await client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "child-of-demo" } });
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var members = body.RootElement.GetProperty("members");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, members.GetArrayLength(), "activating a Task reference must not create a duplicate member");
        Assert.AreEqual("child-of-demo", members[0].GetProperty("taskId").GetString(), "the activated Task must move to the top of the Session Task Set");
        Assert.AreEqual("child-of-demo", body.RootElement.GetProperty("selectedTaskId").GetString(), "the activated Task must become selected");
    }
}
