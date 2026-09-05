using System.Diagnostics;
using System.Net;
using System.Text.Json;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Covers issue #563's performance acceptance criterion for the 20-member
/// rail. Compact rail summaries (no Description/Notes for unselected
/// members) and lazy selected-only full detail are already proven by
/// <c>Tasks_RailSummariesOmitDescriptionAndNotesWhileSelectedMemberGetsFullDetail</c>
/// in <see cref="SessionTaskSetBoundaryTests"/>; bounded watchers are a
/// per-process architectural invariant documented on
/// <see cref="LiveRefreshCoordinator"/> (one shared file/artifact/backlinks
/// watcher regardless of membership count, plus a per-member debounce
/// dictionary that is capped by the Session Task Set's own 20-member limit
/// and pruned on unload) rather than something an HTTP boundary test can
/// observe directly. What remains is a black-box responsiveness check: a
/// full 20-member rail's common actions must complete promptly, not just
/// correctly. The bound below is deliberately generous (an order of
/// magnitude above typical local run times) so it catches a real regression
/// without becoming flaky on a loaded CI machine.
/// </summary>
[TestClass]
public sealed class PerformanceBoundaryTests : CanvasHostTestBase
{
    private static readonly TimeSpan ResponsivenessBound = TimeSpan.FromSeconds(3);

    [TestMethod]
    public async Task Tasks_FullTwentyMemberRailStaysResponsiveForCommonActions()
    {
        var vault = CreateVault();
        var ids = new List<string> { "demo" };
        for (var i = 0; i < 19; i++)
        {
            var id = $"perf-{i}";
            ids.Add(id);
            AddTask(vault, id, $"Perf task {i}");
        }
        await using var host = await StartHost(vault, "session-perf", "credential-perf");
        using var client = AuthorizedClient("credential-perf");

        using var load = await PostJsonAsync(client, $"{host.Url}/api/tasks/load", new { taskIds = ids });
        Assert.AreEqual(HttpStatusCode.OK, load.StatusCode);

        async Task AssertRespondsWithin(string description, Func<Task<JsonResponseResult>> action)
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await action();
            stopwatch.Stop();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, description);
            Assert.IsLessThan(
                ResponsivenessBound,
                stopwatch.Elapsed,
                $"{description} took {stopwatch.Elapsed} for a full 20-member rail, exceeding the {ResponsivenessBound} responsiveness bound.");
        }

        await AssertRespondsWithin("selecting a rail member", () => PostJsonAsync(client, $"{host.Url}/api/tasks/select", new { taskId = "perf-10" }));
        await AssertRespondsWithin("fetching canvas-state", () => GetJsonAsync(client, $"{host.Url}/canvas-state"));
        await AssertRespondsWithin("refreshing the selected member", () => PostJsonAsync(client, $"{host.Url}/api/tasks/refresh-selected"));
        await AssertRespondsWithin("refreshing all members", () => PostJsonAsync(client, $"{host.Url}/api/tasks/refresh-all"));

        using var finalState = await GetJsonAsync(client, $"{host.Url}/api/tasks");
        var body = finalState.Body;
        Assert.AreEqual(20, body.RootElement.GetProperty("members").GetArrayLength(), "the full rail must remain intact after the responsiveness pass");
    }
}
