using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Black-box coverage for issue #560 (ADR 0026): debounced live Vault
/// observation for the Session Task Set canvas. Every test spawns a real
/// <c>Glasswork.CanvasHost</c> process and talks to it purely through HTTP,
/// exactly like <see cref="SessionTaskSetBoundaryTests"/>. Tests that assert
/// on the background <see cref="LiveRefreshCoordinator"/> (rather than a
/// synchronous refresh endpoint) poll with a generous timeout because the
/// watcher reacts on its own thread-pool timing.
/// </summary>
[TestClass]
public sealed class LiveRefreshBoundaryTests : CanvasHostTestBase
{
    /// <summary>Overwrites a Task file with content that fails to parse (no closing frontmatter delimiter), simulating a transient/mid-write read — see <c>FrontmatterParser.Parse</c>.</summary>
    private static void CorruptTaskFile(string vault, string id)
    {
        File.WriteAllText(Path.Combine(vault, "wiki", "todo", $"{id}.md"), "---\nid: " + id + "\ntitle: Broken\n");
    }

    [TestMethod]
    public async Task Tasks_UnselectedMemberFileChange_AutomaticallyRefreshesRailSummaryWithoutReorderingOrReselecting()
    {
        var vault = CreateVault();
        AddTask(vault, "second", "Second task");
        await using var host = await StartHost(vault, "session-live-rail", "credential-live-rail");
        using var client = AuthorizedClient("credential-live-rail");

        // Load order [demo, second] selects "second"; "demo" is an unselected member.
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo", "second" } }));
        AddTask(vault, "demo", "Demo task renamed live", status: "in-progress");

        using var body = await PollUntilAsync(
            cancellationToken => client.GetAsync($"{host.Url}/api/tasks", cancellationToken),
            root => root.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("taskId").GetString() == "demo").GetProperty("title").GetString() == "Demo task renamed live");

        var members = body.RootElement.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("taskId").GetString()).ToArray();
        var demoMember = body.RootElement.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("taskId").GetString() == "demo");

        CollectionAssert.AreEqual(new[] { "second", "demo" }, members, "a background change to an unselected member must not reorder recency");
        Assert.AreEqual("in-progress", demoMember.GetProperty("statusValue").GetString(), "the changed member's rail summary must reflect the new status");
        Assert.AreEqual("second", body.RootElement.GetProperty("selectedTaskId").GetString(), "a background change to an unselected member must not change selection");
    }

    [TestMethod]
    public async Task Tasks_TransientReadFailureRetainsLastGoodMemberDataAndMarksStale_ThenClearsOnRecovery()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-live-stale", "credential-live-stale");
        using var client = AuthorizedClient("credential-live-stale");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));

        CorruptTaskFile(vault, "demo");
        var staleResponse = await client.PostAsync($"{host.Url}/api/tasks/refresh-all", null);
        using var staleBody = (await ReadJsonResponseAsync(staleResponse)).Body;
        var staleMember = staleBody.RootElement.GetProperty("members").EnumerateArray().Single();

        AddTask(vault, "demo", "Demo task");
        var recoveredResponse = await client.PostAsync($"{host.Url}/api/tasks/refresh-all", null);
        using var recoveredBody = (await ReadJsonResponseAsync(recoveredResponse)).Body;
        var recoveredMember = recoveredBody.RootElement.GetProperty("members").EnumerateArray().Single();

        Assert.IsFalse(staleMember.GetProperty("isUnavailable").GetBoolean(), "a transient parse failure must never be reported as Unavailable");
        Assert.IsTrue(staleMember.GetProperty("isStale").GetBoolean(), "a transient parse failure must mark the member stale");
        Assert.IsFalse(string.IsNullOrEmpty(staleMember.GetProperty("staleError").GetString()), "the exact stale error must be visible");
        Assert.AreEqual("Demo task", staleMember.GetProperty("title").GetString(), "last-good title must be retained through a transient failure, never blanked");
        Assert.IsFalse(recoveredMember.GetProperty("isStale").GetBoolean(), "a later successful refresh must clear the stale flag");
    }

    [TestMethod]
    public async Task Tasks_RefreshAllReportsPerMemberOutcomesForStaleAndUnavailableMembers()
    {
        var vault = CreateVault();
        AddTask(vault, "second", "Second task");
        AddTask(vault, "vanishing", "Vanishing task");
        await using var host = await StartHost(vault, "session-live-outcomes", "credential-live-outcomes");
        using var client = AuthorizedClient("credential-live-outcomes");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo", "second", "vanishing" } }));

        CorruptTaskFile(vault, "second");
        File.Delete(Path.Combine(vault, "wiki", "todo", "vanishing.md"));
        var response = await client.PostAsync($"{host.Url}/api/tasks/refresh-all", null);
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var outcomes = body.RootElement.GetProperty("outcomes").EnumerateArray().ToDictionary(o => o.GetProperty("taskId").GetString()!, o => o);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(outcomes["demo"].GetProperty("ok").GetBoolean(), "an untouched member must report a successful outcome");
        Assert.IsFalse(outcomes["second"].GetProperty("ok").GetBoolean(), "a stale member must report a failed outcome");
        Assert.IsFalse(string.IsNullOrEmpty(outcomes["second"].GetProperty("error").GetString()));
        Assert.IsFalse(outcomes["vanishing"].GetProperty("ok").GetBoolean(), "an unavailable member must report a failed outcome");
    }

    [TestMethod]
    public async Task Tasks_ManualRefreshEndpointRetriesOneUnselectedMemberWithoutRequiringSelection()
    {
        var vault = CreateVault();
        AddTask(vault, "second", "Second task");
        await using var host = await StartHost(vault, "session-live-retry", "credential-live-retry");
        using var client = AuthorizedClient("credential-live-retry");
        // Load order [demo, second] selects "second"; "demo" stays unselected.
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo", "second" } }));

        CorruptTaskFile(vault, "demo");
        AddTask(vault, "demo", "Demo task recovered");
        var response = await client.PostAsJsonAsync($"{host.Url}/api/tasks/refresh", new { taskId = "demo" });
        using var body = (await ReadJsonResponseAsync(response)).Body;
        var demoMember = body.RootElement.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("taskId").GetString() == "demo");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("second", body.RootElement.GetProperty("selectedTaskId").GetString(), "retrying an unselected member must not change selection");
        Assert.AreEqual("Demo task recovered", demoMember.GetProperty("title").GetString());
        Assert.IsFalse(demoMember.GetProperty("isStale").GetBoolean());

        var notMemberResponse = await client.PostAsJsonAsync($"{host.Url}/api/tasks/refresh", new { taskId = "never-loaded" });
        Assert.AreEqual(HttpStatusCode.NotFound, notMemberResponse.StatusCode, "retrying an id that is not a loaded member must fail visibly");
    }

    [TestMethod]
    public async Task Tasks_SelectedDetailFallsBackToLastGoodProjectionAndSurfacesStaleBanner_OnTransientFailure()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-live-detail-stale", "credential-live-detail-stale");
        using var client = AuthorizedClient("credential-live-detail-stale");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));
        var before = await client.GetAsync($"{host.Url}/canvas-state");
        using var beforeBody = (await ReadJsonResponseAsync(before)).Body;
        var goodDescriptionHtml = beforeBody.RootElement.GetProperty("selectedDetail").GetProperty("projection").GetProperty("descriptionHtml").GetString();

        CorruptTaskFile(vault, "demo");
        var during = await client.GetAsync($"{host.Url}/canvas-state");
        using var duringBody = (await ReadJsonResponseAsync(during)).Body;
        var selectedDetail = duringBody.RootElement.GetProperty("selectedDetail");

        AddTask(vault, "demo", "Demo task");
        var after = await client.GetAsync($"{host.Url}/canvas-state");
        using var afterBody = (await ReadJsonResponseAsync(after)).Body;

        Assert.AreEqual("task", selectedDetail.GetProperty("kind").GetString(), "a transient full-detail rebuild failure must not become a bare error card");
        Assert.IsTrue(selectedDetail.GetProperty("isStale").GetBoolean());
        Assert.IsFalse(string.IsNullOrEmpty(selectedDetail.GetProperty("staleError").GetString()));
        Assert.AreEqual(goodDescriptionHtml, selectedDetail.GetProperty("projection").GetProperty("descriptionHtml").GetString(), "the last-good full projection must be retained, not blanked");
        Assert.IsFalse(afterBody.RootElement.GetProperty("selectedDetail").GetProperty("isStale").GetBoolean(), "a later successful poll must clear the stale flag");
    }

    [TestMethod]
    public async Task Tasks_LiveWatcherAutomaticallyRefreshesSelectedMembersRailSummaryWithoutManualRefresh()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-live-selected", "credential-live-selected");
        using var client = AuthorizedClient("credential-live-selected");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));

        AddTask(vault, "demo", "Demo task live-updated", status: "done");

        using var body = await PollUntilAsync(
            cancellationToken => client.GetAsync($"{host.Url}/canvas-state", cancellationToken),
            root => root.GetProperty("members")[0].GetProperty("title").GetString() == "Demo task live-updated");

        Assert.AreEqual("done", body.RootElement.GetProperty("members")[0].GetProperty("statusValue").GetString());
        StringAssert.Contains(
            body.RootElement.GetProperty("selectedDetail").GetProperty("projection").GetProperty("title").GetString(),
            "Demo task live-updated",
            "the selected member's full detail must also reflect the change without a manual refresh call");
    }

    [TestMethod]
    public async Task Tasks_ArtifactChangeToSelectedMemberBumpsLastUpdatedTimestampWithoutRequiringManualRefresh()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-live-artifact", "credential-live-artifact");
        using var client = AuthorizedClient("credential-live-artifact");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));
        var before = await client.GetAsync($"{host.Url}/api/tasks");
        using var beforeBody = (await ReadJsonResponseAsync(before)).Body;
        var beforeTimestamp = beforeBody.RootElement.TryGetProperty("lastUpdatedUtc", out var beforeValue) && beforeValue.ValueKind == JsonValueKind.String
            ? beforeValue.GetString()
            : null;

        var artifactsFolder = Path.Combine(vault, "wiki", "todo", "demo.artifacts");
        Directory.CreateDirectory(artifactsFolder);
        await File.WriteAllTextAsync(Path.Combine(artifactsFolder, "notes.md"), "# Live artifact\n");

        using var body = await PollUntilAsync(
            cancellationToken => client.GetAsync($"{host.Url}/api/tasks", cancellationToken),
            root => root.TryGetProperty("lastUpdatedUtc", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() != beforeTimestamp);

        Assert.IsTrue(body.RootElement.TryGetProperty("lastUpdatedUtc", out var updated) && updated.ValueKind == JsonValueKind.String);
    }

    [TestMethod]
    public async Task LiveRefresh_UnloadedMembersDebounceStateIsDroppedAndFileChangesNeverResurrectThem()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-live-unload", "credential-live-unload");
        using var client = AuthorizedClient("credential-live-unload");
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));
        await AssertJsonSuccessAsync(client.PostAsJsonAsync($"{host.Url}/api/tasks/unload", new { taskId = "demo" }));

        // Rapid changes to a no-longer-loaded Task's file must never crash the
        // host or resurrect it as a member — the coordinator only reacts to
        // Task IDs that are current members (checked both at schedule time and
        // fire time).
        AddTask(vault, "demo", "Demo task changed after unload");
        AddTask(vault, "demo", "Demo task changed again after unload");
        await Task.Delay(1000);

        var health = await client.GetAsync($"{host.Url}/health");
        var state = await client.GetAsync($"{host.Url}/api/tasks");
        using var stateBody = (await ReadJsonResponseAsync(state)).Body;

        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, "the host must stay healthy after a change to an unloaded Task's file");
        Assert.AreEqual(0, stateBody.RootElement.GetProperty("members").GetArrayLength(), "a file change must never resurrect a Task that was explicitly unloaded");
    }
}
