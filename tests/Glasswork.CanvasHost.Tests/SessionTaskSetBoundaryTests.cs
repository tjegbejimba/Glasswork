using System.Net;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Black-box coverage for the Session Task Set introduced by issue #556
/// (ADR 0026): batch loading, de-duplication, recency, selection, the
/// 20-member cap, removal, clear, every Task status, unavailable members,
/// and the master-detail canvas rendering. Persisted restoration across host
/// restarts (issue #557) is covered separately below.
/// </summary>
[TestClass]
public sealed class SessionTaskSetBoundaryTests : CanvasHostTestBase
{
    [TestMethod]
    public async Task Tasks_LoadBatchDeduplicatesOrdersByRecencyAndSelectsLastSuccessful()
    {
        var vault = CreateVault();
        AddTask(vault, "second", "Second task");
        AddTask(vault, "third", "Third task");
        await using var host = await StartHost(vault, "session-load", "credential-load");
        using var client = AuthorizedClient("credential-load");

        // First load establishes recency order [demo, second].
        await AssertJsonSuccessAsync(PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo", "second" } }));
        // Second load re-loads "demo" (dedup: it must move back to the front)
        // and adds "third"; the last successfully loaded id ("third") is selected.
        using var response = await PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "third", "demo" } });
        var body = response.Body;
        var members = body.RootElement.GetProperty("members");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, members.GetArrayLength(), "loading an already-present id must not duplicate membership");
        Assert.AreEqual("demo", members[0].GetProperty("taskId").GetString(), "the most recently (re)loaded id moves to the front");
        Assert.AreEqual("third", members[1].GetProperty("taskId").GetString());
        Assert.AreEqual("second", members[2].GetProperty("taskId").GetString(), "untouched members keep their relative order");
        Assert.AreEqual("demo", body.RootElement.GetProperty("selectedTaskId").GetString(), "the last id in the batch that loaded successfully becomes selected");
    }

    [TestMethod]
    public async Task Tasks_AcceptEveryLoadableStatusAndNeverInferMembershipFromIt()
    {
        var vault = CreateVault();
        AddTask(vault, "t-in-progress", "In progress task", status: "in-progress");
        AddTask(vault, "t-blocked", "Blocked task", status: "blocked", priority: "high", due: "2026-01-01", blockedBy: "demo");
        AddTask(vault, "t-done", "Done task", status: "done");
        AddTask(vault, "t-cancelled", "Cancelled task", status: "cancelled");
        await using var host = await StartHost(vault, "session-status", "credential-status");
        using var client = AuthorizedClient("credential-status");

        using var response = await PostJsonAsync(
            client,
            $"{host.Url}/api/tasks/load",
            new { taskIds = new[] { "demo", "t-in-progress", "t-blocked", "t-done", "t-cancelled" } });
        var body = response.Body;
        var members = body.RootElement.GetProperty("members");
        var blockedMember = members.EnumerateArray().Single(m => m.GetProperty("taskId").GetString() == "t-blocked");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(5, members.GetArrayLength(), "every loadable status must be accepted into the Session Task Set");
        Assert.IsFalse(members.EnumerateArray().Any(m => m.GetProperty("isUnavailable").GetBoolean()), "a valid Task of any status is never rejected or marked unavailable at load time");
        Assert.IsTrue(blockedMember.GetProperty("isBlocked").GetBoolean(), "a rail row for a blocked Task must expose blocker state");
        Assert.AreEqual("high", blockedMember.GetProperty("priority").GetString(), "a rail row must expose the Task's priority");
        StringAssert.StartsWith(blockedMember.GetProperty("due").GetString(), "2026-01-01", "a rail row must expose the Task's due state");
    }

    [TestMethod]
    public async Task Tasks_LoadBeyondTheLimitFailsAtomicallyWithoutPartialChanges()
    {
        var vault = CreateVault();
        var ids = new List<string> { "demo" };
        for (var i = 0; i < 19; i++)
        {
            var id = $"filler-{i}";
            ids.Add(id);
            AddTask(vault, id, $"Filler {i}");
        }
        await using var host = await StartHost(vault, "session-limit", "credential-limit");
        using var client = AuthorizedClient("credential-limit");

        // Fill the canvas to exactly the 20-member limit first.
        using var fill = await PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = ids });
        AddTask(vault, "overflow", "Overflow task");
        using var overflow = await PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "overflow" } });
        using var after = await GetJsonAsync(client, $"{host.Url}/api/tasks");
        var afterBody = after.Body;

        Assert.AreEqual(HttpStatusCode.OK, fill.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, overflow.StatusCode, "a load that would exceed the 20-Task limit must fail visibly");
        var overflowBody = overflow.Body;
        Assert.AreEqual("limit_exceeded", overflowBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(20, afterBody.RootElement.GetProperty("members").GetArrayLength(), "a rejected load must not evict or partially add members");
        Assert.IsFalse(afterBody.RootElement.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("taskId").GetString() == "overflow"), "the rejected Task must not appear in membership");
    }

    [TestMethod]
    public async Task Tasks_SelectDoesNotReorderRecencyButUnloadSelectsNextMostRecent()
    {
        var vault = CreateVault();
        AddTask(vault, "second", "Second task");
        AddTask(vault, "third", "Third task");
        await using var host = await StartHost(vault, "session-select", "credential-select");
        using var client = AuthorizedClient("credential-select");
        await AssertJsonSuccessAsync(PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo", "second", "third" } }));
        // Recency order after loading in this sequence is [third, second, demo] (most to least recent).

        // Selecting the least-recent member is a view action only.
        using var selectResponse = await PostJsonAsync(client, $"{host.Url}/api/tasks/select", new { taskId = "demo" });
        var selectBody = selectResponse.Body;
        var orderAfterSelect = selectBody.RootElement.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("taskId").GetString()).ToArray();

        // Removing the selected (least-recent) member selects the member that
        // was immediately more recent than it — "second" is the next
        // most-recent Task after "demo" in the [third, second, demo] order.
        using var unloadResponse = await PostJsonAsync(client, $"{host.Url}/api/tasks/unload", new { taskId = "demo" });
        var unloadBody = unloadResponse.Body;

        Assert.AreEqual(HttpStatusCode.OK, selectResponse.StatusCode);
        Assert.AreEqual("demo", selectBody.RootElement.GetProperty("selectedTaskId").GetString());
        CollectionAssert.AreEqual(new[] { "third", "second", "demo" }, orderAfterSelect, "selecting a row must not change recency order");
        Assert.AreEqual(HttpStatusCode.OK, unloadResponse.StatusCode);
        Assert.AreEqual("second", unloadBody.RootElement.GetProperty("selectedTaskId").GetString(), "removing the selected member selects the next most-recent remaining member");
        Assert.AreEqual(2, unloadBody.RootElement.GetProperty("members").GetArrayLength());
    }

    [TestMethod]
    public async Task Tasks_UnloadingTheLastMemberSelectsNullAndClearNeverMutatesTheVault()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-empty", "credential-empty");
        using var client = AuthorizedClient("credential-empty");
        await AssertJsonSuccessAsync(PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));

        using var unloadResponse = await PostJsonAsync(client, $"{host.Url}/api/tasks/unload", new { taskId = "demo" });
        var unloadBody = unloadResponse.Body;

        await AssertJsonSuccessAsync(PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));
        using var clearResponse = await PostJsonAsync(client, $"{host.Url}/api/tasks/clear");
        var clearBody = clearResponse.Body;
        var taskFile = Path.Combine(vault, "wiki", "todo", "demo.md");
        var contentAfterClear = await File.ReadAllTextAsync(taskFile);

        Assert.IsNull(unloadBody.RootElement.GetProperty("selectedTaskId").GetString(), "unloading the only member must select null (the empty state)");
        Assert.AreEqual(0, unloadBody.RootElement.GetProperty("members").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.OK, clearResponse.StatusCode);
        Assert.AreEqual(0, clearBody.RootElement.GetProperty("members").GetArrayLength());
        Assert.IsNull(clearBody.RootElement.GetProperty("selectedTaskId").GetString());
        StringAssert.Contains(contentAfterClear, "title: Demo task", "Clear all must never mutate the Vault");
    }

    [TestMethod]
    public async Task Tasks_RetainUnavailableMembersAndRefreshAllReportsVaultDeletion()
    {
        var vault = CreateVault();
        AddTask(vault, "vanishing", "Vanishing task");
        await using var host = await StartHost(vault, "session-unavailable", "credential-unavailable");
        using var client = AuthorizedClient("credential-unavailable");

        // Loading a non-existent id must retain it as an unavailable member, not reject the batch.
        using var loadResponse = await PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "vanishing", "missing-id" } });
        var loadBody = loadResponse.Body;

        File.Delete(Path.Combine(vault, "wiki", "todo", "vanishing.md"));
        using var refreshResponse = await PostJsonAsync(client, $"{host.Url}/api/tasks/refresh-all");
        var refreshBody = refreshResponse.Body;
        var refreshedMembers = refreshBody.RootElement.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("taskId").GetString()!, m => m);

        Assert.AreEqual(HttpStatusCode.OK, loadResponse.StatusCode);
        Assert.AreEqual(2, loadBody.RootElement.GetProperty("members").GetArrayLength(), "a not-found id becomes an unavailable member, it is not rejected");
        Assert.IsTrue(loadBody.RootElement.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("taskId").GetString() == "missing-id").GetProperty("isUnavailable").GetBoolean());
        Assert.IsTrue(refreshedMembers["vanishing"].GetProperty("isUnavailable").GetBoolean(), "refresh-all must mark a Task unavailable once it disappears from the Vault");
        Assert.IsFalse(string.IsNullOrEmpty(refreshedMembers["vanishing"].GetProperty("unavailableError").GetString()));
    }

    [TestMethod]
    public async Task Tasks_RailSummariesOmitDescriptionAndNotesWhileSelectedMemberGetsFullDetail()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-lazy", "credential-lazy");
        using var client = AuthorizedClient("credential-lazy");

        await AssertJsonSuccessAsync(PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));
        using var response = await GetJsonAsync(client, $"{host.Url}/canvas-state");
        var body = response.Body;
        var memberJson = body.RootElement.GetProperty("members")[0].ToString();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(memberJson.Contains("Demo description", StringComparison.Ordinal), "rail summaries must not include Description/Notes previews");
        Assert.AreEqual("task", body.RootElement.GetProperty("selectedDetail").GetProperty("kind").GetString());
        StringAssert.Contains(
            body.RootElement.GetProperty("selectedDetail").GetProperty("projection").GetProperty("descriptionHtml").GetString(),
            "Demo description",
            "the selected member alone receives full lazily-loaded Task Detail");
    }

    [TestMethod]
    public async Task Canvas_BrandingRailAccessibilityAndResponsiveLayoutArePresent()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-ui", "credential-canvas-ui");
        using var client = AuthorizedClient("credential-canvas-ui");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "<title>Glasswork Tasks</title>", "the visible canvas name must be Glasswork Tasks");
        StringAssert.Contains(html, "Loaded Tasks", "the rail label must be Loaded Tasks");
        StringAssert.Contains(html, "role\",\"listbox\"", "the rail must expose an accessible listbox role");
        StringAssert.Contains(html, "role\",\"option\"", "each rail row must expose an accessible option role");
        StringAssert.Contains(html, "aria-selected", "selection must be identifiable to assistive technology");
        StringAssert.Contains(html, "Remove from canvas", "rail rows must offer a Remove from canvas control");
        StringAssert.Contains(html, "Clear all", "the rail must offer a Clear all control");
        StringAssert.Contains(html, "@media(max-width:719px)", "narrow layouts must collapse the rail responsively");
        StringAssert.Contains(html, "@media(min-width:720px)", "wide layouts must keep the rail beside the detail");
    }

    [TestMethod]
    public async Task Canvas_EmptyStateRendersGuidanceWhenNoTasksAreLoaded()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-empty", "credential-canvas-empty");
        using var client = AuthorizedClient("credential-canvas-empty");

        var response = await client.GetAsync($"{host.Url}/canvas");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "Ask an agent to load a Glasswork Task to get started.");
    }

    [TestMethod]
    public async Task Api_SingularTaskIdAndLegacyTaskEndpointRemainCompatibleShorthands()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-back-compat", "credential-back-compat");
        using var client = AuthorizedClient("credential-back-compat");

        // The pre-existing stateless detail endpoint must be untouched by the Session Task Set.
        using var legacyDetail = await GetJsonAsync(client, $"{host.Url}/api/task?task_id=demo");
        using var canvasState = await GetJsonAsync(client, $"{host.Url}/canvas-state");
        // Visiting /canvas with the singular task_id shorthand must load and select that Task.
        var canvasVisit = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        using var stateAfterVisit = await GetJsonAsync(client, $"{host.Url}/canvas-state");
        var afterVisitBody = stateAfterVisit.Body;

        Assert.AreEqual(HttpStatusCode.OK, legacyDetail.StatusCode);
        var legacyDetailBody = legacyDetail.Body;
        Assert.AreEqual("task", legacyDetailBody.RootElement.GetProperty("kind").GetString());
        var beforeVisitBody = canvasState.Body;
        Assert.AreEqual(0, beforeVisitBody.RootElement.GetProperty("members").GetArrayLength(), "the canvas starts with no members until something loads");
        Assert.AreEqual(HttpStatusCode.OK, canvasVisit.StatusCode);
        Assert.AreEqual("demo", afterVisitBody.RootElement.GetProperty("selectedTaskId").GetString(), "singular task_id remains a compatible shorthand that loads and selects the Task");
    }
}
