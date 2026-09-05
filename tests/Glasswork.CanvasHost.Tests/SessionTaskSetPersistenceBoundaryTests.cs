using System.Net;
using System.Text.Json;
using Glasswork.Core.Services;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Black-box coverage for issue #557 (ADR 0026): the Session Task Set
/// persists across canvas host restarts through Glasswork's existing
/// cross-process-safe UI State, keyed by Copilot session ID. Every test
/// spawns real <c>Glasswork.CanvasHost</c> processes and talks to them purely
/// through HTTP, exactly like <see cref="SessionTaskSetBoundaryTests"/>.
/// </summary>
[TestClass]
public sealed class SessionTaskSetPersistenceBoundaryTests : CanvasHostTestBase
{
    [TestMethod]
    public async Task Restore_RebuildsMembershipAndRecencyOrderAndSelectsMostRecent_AfterHostRestart()
    {
        var vault = CreateVault();
        AddTask(vault, "second", "Second task");
        AddTask(vault, "third", "Third task");
        var uiStatePath = NewUiStatePath();

        await using (var first = await StartHost(vault, "session-restore", "credential-restore", uiStatePath))
        {
            using var client = AuthorizedClient("credential-restore");
            // Recency order after this batch is [third, second, demo] (see
            // SessionTaskSetBoundaryTests for why); "third" is selected.
            await AssertJsonSuccessAsync(PostJsonAsync(client, $"{first.Url}/api/tasks/load", new { taskIds = new[] { "demo", "second", "third" } }));
        }

        // A brand-new host process for the SAME session id, reading the SAME
        // UI State file, simulates the canvas reopening after a cold resume.
        await using var restarted = await StartHost(vault, "session-restore", "credential-restore", uiStatePath);
        using var restartedClient = AuthorizedClient("credential-restore");
        using var response = await GetJsonAsync(restartedClient, $"{restarted.Url}/api/tasks");
        var body = response.Body;
        var order = body.RootElement.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("taskId").GetString()).ToArray();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { "third", "second", "demo" }, order, "membership and recency order must survive a host restart");
        Assert.AreEqual("third", body.RootElement.GetProperty("selectedTaskId").GetString(), "restoring must select the most-recent member");
        Assert.IsFalse(body.RootElement.TryGetProperty("restoreError", out var restoreError) && restoreError.ValueKind != JsonValueKind.Null, "a clean restore must not report a restore error");
    }

    [TestMethod]
    public async Task Restore_RetainsUnavailableMemberWithLastKnownTitleAndError_WhenTaskWasDeletedWhileHostWasDown()
    {
        var vault = CreateVault();
        AddTask(vault, "vanishing", "Vanishing task");
        var uiStatePath = NewUiStatePath();

        await using (var first = await StartHost(vault, "session-restore-unavailable", "credential-restore-unavailable", uiStatePath))
        {
            using var client = AuthorizedClient("credential-restore-unavailable");
            await AssertJsonSuccessAsync(PostJsonAsync(client, $"{first.Url}/api/tasks/load", new { taskIds = new[] { "vanishing" } }));
        }

        // The Task disappears from the Vault while no host is running.
        File.Delete(Path.Combine(vault, "wiki", "todo", "vanishing.md"));

        await using var restarted = await StartHost(vault, "session-restore-unavailable", "credential-restore-unavailable", uiStatePath);
        using var restartedClient = AuthorizedClient("credential-restore-unavailable");
        using var response = await GetJsonAsync(restartedClient, $"{restarted.Url}/api/tasks");
        var body = response.Body;
        var member = body.RootElement.GetProperty("members").EnumerateArray().Single();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(member.GetProperty("isUnavailable").GetBoolean(), "a Task missing at restore time must come back as an unavailable member, not silently vanish");
        Assert.AreEqual("Vanishing task", member.GetProperty("title").GetString(), "the last-known title must come from persisted state");
        Assert.IsFalse(string.IsNullOrEmpty(member.GetProperty("unavailableError").GetString()), "the exact restore-time error must be visible");
    }

    [TestMethod]
    public async Task Restore_EmptiesTheCanvas_AfterClearPersistsAcrossHostRestart()
    {
        var vault = CreateVault();
        var uiStatePath = NewUiStatePath();

        await using (var first = await StartHost(vault, "session-restore-clear", "credential-restore-clear", uiStatePath))
        {
            using var client = AuthorizedClient("credential-restore-clear");
            await AssertJsonSuccessAsync(PostJsonAsync(client, $"{first.Url}/api/tasks/load", new { taskIds = new[] { "demo" } }));
            await AssertJsonSuccessAsync(PostJsonAsync(client, $"{first.Url}/api/tasks/clear"));
        }

        await using var restarted = await StartHost(vault, "session-restore-clear", "credential-restore-clear", uiStatePath);
        using var restartedClient = AuthorizedClient("credential-restore-clear");
        using var response = await GetJsonAsync(restartedClient, $"{restarted.Url}/api/tasks");
        var body = response.Body;
        var taskFile = Path.Combine(vault, "wiki", "todo", "demo.md");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, body.RootElement.GetProperty("members").GetArrayLength(), "an explicitly cleared Session Task Set must restore empty, not repopulate");
        Assert.IsNull(body.RootElement.GetProperty("selectedTaskId").GetString());
        StringAssert.Contains(await File.ReadAllTextAsync(taskFile), "title: Demo task", "Clear all must never mutate the Vault");
    }

    [TestMethod]
    public async Task Isolation_TwoSessionsSharingOneUiStateFile_RestoreOnlyTheirOwnMembership()
    {
        var vault = CreateVault();
        AddTask(vault, "task-a", "Task A");
        AddTask(vault, "task-b", "Task B");
        var uiStatePath = NewUiStatePath();

        // Two concurrent hosts backing different Copilot sessions, sharing
        // one Vault and one UI State file.
        await using (var hostA = await StartHost(vault, "session-iso-a", "credential-iso-a", uiStatePath))
        await using (var hostB = await StartHost(vault, "session-iso-b", "credential-iso-b", uiStatePath))
        {
            using var clientA = AuthorizedClient("credential-iso-a");
            using var clientB = AuthorizedClient("credential-iso-b");

            // Interleave the two hosts' explicit loads to exercise concurrent
            // merge-on-save rather than a strictly sequential save order.
            var loadA = PostJsonAsync(clientA, $"{hostA.Url}/api/tasks/load", new { taskIds = new[] { "task-a" } });
            var loadB = PostJsonAsync(clientB, $"{hostB.Url}/api/tasks/load", new { taskIds = new[] { "task-b" } });
            var loadResponses = await Task.WhenAll(loadA, loadB);
            foreach (var loadResponse in loadResponses)
            {
                using (loadResponse)
                    Assert.AreEqual(HttpStatusCode.OK, loadResponse.StatusCode);
            }

            using var stateA = await GetJsonAsync(clientA, $"{hostA.Url}/api/tasks");
            using var stateB = await GetJsonAsync(clientB, $"{hostB.Url}/api/tasks");
            var bodyA = stateA.Body;
            var bodyB = stateB.Body;

            Assert.AreEqual(1, bodyA.RootElement.GetProperty("members").GetArrayLength(), "session-iso-a's own host must see only its own membership");
            Assert.AreEqual("task-a", bodyA.RootElement.GetProperty("members")[0].GetProperty("taskId").GetString());
            Assert.AreEqual(1, bodyB.RootElement.GetProperty("members").GetArrayLength(), "session-iso-b's own host must see only its own membership");
            Assert.AreEqual("task-b", bodyB.RootElement.GetProperty("members")[0].GetProperty("taskId").GetString());
        }

        // Cross-session read: session-iso-b's credential must never see
        // session-iso-a's canvas, and vice versa — even for a fresh process.
        await using var restartedA = await StartHost(vault, "session-iso-a", "credential-iso-a", uiStatePath);
        await using var restartedB = await StartHost(vault, "session-iso-b", "credential-iso-b", uiStatePath);
        using var restartedClientA = AuthorizedClient("credential-iso-a");
        using var restartedClientB = AuthorizedClient("credential-iso-b");
        using var restoredA = await GetJsonAsync(restartedClientA, $"{restartedA.Url}/api/tasks");
        using var restoredB = await GetJsonAsync(restartedClientB, $"{restartedB.Url}/api/tasks");
        var restoredBodyA = restoredA.Body;
        var restoredBodyB = restoredB.Body;

        Assert.AreEqual("task-a", restoredBodyA.RootElement.GetProperty("members")[0].GetProperty("taskId").GetString(), "restoring session-iso-a must never pick up session-iso-b's membership");
        Assert.AreEqual("task-b", restoredBodyB.RootElement.GetProperty("members")[0].GetProperty("taskId").GetString(), "restoring session-iso-b must never pick up session-iso-a's membership");
    }

    [TestMethod]
    public async Task Restore_FailsVisibly_WhenPersistedStateIsAnUnrecognizedVersion_AndClearSelfHeals()
    {
        var vault = CreateVault();
        var uiStatePath = NewUiStatePath();
        var uiState = new JsonFileUiStateService(uiStatePath);
        uiState.Set(SessionTaskSetStateStore.KeyPrefix + "session-future-version", new
        {
            version = 99,
            members = new[] { new { taskId = "demo", title = "Demo task" } },
        });
        uiState.Save();

        await using var host = await StartHost(vault, "session-future-version", "credential-future-version", uiStatePath);
        using var client = AuthorizedClient("credential-future-version");

        using var apiResponse = await GetJsonAsync(client, $"{host.Url}/api/tasks");
        var apiBody = apiResponse.Body;
        var canvasResponse = await client.GetAsync($"{host.Url}/canvas");
        var html = await canvasResponse.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, apiResponse.StatusCode);
        Assert.AreEqual(0, apiBody.RootElement.GetProperty("members").GetArrayLength(), "an unreadable persisted version must never silently repopulate as if it were valid");
        var restoreError = apiBody.RootElement.GetProperty("restoreError");
        Assert.AreEqual(JsonValueKind.Object, restoreError.ValueKind, "a future/unsupported version must fail visibly, not look like an ordinary empty canvas");
        Assert.AreEqual("unsupported_version", restoreError.GetProperty("code").GetString());
        StringAssert.Contains(html, "Couldn't restore the Loaded Tasks", "the canvas HTML must render a visible restore failure banner");

        // Explicit Clear all must self-heal: it is always available (even
        // with zero members) and overwrites the malformed entry.
        using var clearResponse = await PostJsonAsync(client, $"{host.Url}/api/tasks/clear");
        var clearBody = clearResponse.Body;
        Assert.AreEqual(HttpStatusCode.OK, clearResponse.StatusCode);
        Assert.IsFalse(clearBody.RootElement.TryGetProperty("restoreError", out var clearedError) && clearedError.ValueKind != JsonValueKind.Null, "clearing must supersede the prior restore failure");
    }

    [TestMethod]
    public async Task Restore_FailsVisibly_WhenPersistedStateIsMalformed()
    {
        var vault = CreateVault();
        var uiStatePath = NewUiStatePath();
        var uiState = new JsonFileUiStateService(uiStatePath);
        uiState.Set(SessionTaskSetStateStore.KeyPrefix + "session-malformed", "not an object");
        uiState.Save();

        await using var host = await StartHost(vault, "session-malformed", "credential-malformed", uiStatePath);
        using var client = AuthorizedClient("credential-malformed");

        using var response = await GetJsonAsync(client, $"{host.Url}/api/tasks");
        var body = response.Body;

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(0, body.RootElement.GetProperty("members").GetArrayLength());
        Assert.AreEqual("malformed_state", body.RootElement.GetProperty("restoreError").GetProperty("code").GetString());
    }
}
