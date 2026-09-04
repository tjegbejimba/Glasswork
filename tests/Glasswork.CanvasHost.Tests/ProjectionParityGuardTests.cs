using System.Reflection;
using System.Text.Json;
using Glasswork.Core.Models;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Guards issue #563's "native/canvas parity" invariant at the seam most
/// likely to silently drift: <see cref="TaskDetailProjection"/> is the one
/// presentation-neutral read model native Task Detail binds to directly, and
/// the canvas host is expected to reflect every one of its properties in the
/// <c>/api/task</c> JSON payload (ADR 0026). Native binding is compile-time
/// checked by the C# compiler, so it cannot silently drop a projection
/// property; the canvas's JSON boundary has no such guarantee. If a future
/// change to <see cref="TaskDetailProjection"/> adds a property that never
/// reaches the canvas (e.g. someone replaces the wholesale
/// <c>JsonSerializer.SerializeToNode(projection, ...)</c> in
/// <c>Program.EnrichProjection</c> with a hand-picked subset), this test
/// fails the build instead of shipping a silently narrower canvas view.
/// </summary>
[TestClass]
public sealed class ProjectionParityGuardTests : CanvasHostTestBase
{
    [TestMethod]
    public async Task ApiTask_SerializesEveryTaskDetailProjectionPropertyForCanvas()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-parity", "credential-parity");
        using var client = AuthorizedClient("credential-parity");

        var response = await client.GetAsync($"{host.Url}/api/task?task_id=demo");
        var body = await response.Content.ReadAsStringAsync();
        var projectionJson = JsonDocument.Parse(body).RootElement.GetProperty("projection");

        var propertyNames = typeof(TaskDetailProjection)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();
        Assert.IsGreaterThan(10, propertyNames.Count, "Reflection found suspiciously few TaskDetailProjection properties; the guard may not be inspecting the expected type.");

        var missing = propertyNames
            .Where(name => !projectionJson.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(name), out _))
            .ToList();

        Assert.IsEmpty(
            missing,
            "The canvas /api/task response is missing JSON keys for these TaskDetailProjection properties: " +
            string.Join(", ", missing) +
            ". Every semantic property on TaskDetailProjection must reach the canvas payload (ADR 0026, issue #563) " +
            "-- update Program.EnrichProjection (or the property's [JsonIgnore]) so both renderers stay in sync.");
    }
}
