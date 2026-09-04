using System.Net;
using static Glasswork.CanvasHost.Tests.CanvasHostTestSupport;

namespace Glasswork.CanvasHost.Tests;

/// <summary>
/// Black-box coverage for issue #563's accessibility acceptance criterion:
/// "names, roles, selection state, focus order, keyboard operation, live
/// update announcements, reduced-motion behavior where applicable, and
/// contrast under host theme tokens." Names/roles/selection state are
/// already covered by <c>Canvas_BrandingRailAccessibilityAndResponsiveLayoutArePresent</c>
/// in <see cref="SessionTaskSetBoundaryTests"/>; this class covers the
/// remaining items. These tests cannot execute the emitted JavaScript in a
/// real browser, so they assert on the served markup/script/CSS text for the
/// behaviors a real browser would exhibit -- the same pattern the existing
/// suite already uses for ARIA roles and responsive breakpoints.
/// </summary>
[TestClass]
public sealed class CanvasAccessibilityAndMotionTests
{
    [TestMethod]
    public async Task Canvas_RailSupportsArrowKeyNavigationAmongOptions()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-keys", "credential-canvas-keys");
        using var client = AuthorizedClient("credential-canvas-keys");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "\"keydown\"", "the rail must support keyboard operation beyond native Tab focus");
        StringAssert.Contains(html, "ArrowDown", "Down arrow must move focus to the next rail option");
        StringAssert.Contains(html, "ArrowUp", "Up arrow must move focus to the previous rail option");
        StringAssert.Contains(html, "\"Home\"", "Home must move focus to the first rail option");
        StringAssert.Contains(html, "\"End\"", "End must move focus to the last rail option");
    }

    [TestMethod]
    public async Task Canvas_UpdatedTimestampIsAPoliteLiveRegion()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-live", "credential-canvas-live");
        using var client = AuthorizedClient("credential-canvas-live");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "aria-live", "auto-refresh (issue #560) updates must be announced to assistive technology");
        StringAssert.Contains(html, "\"polite\"", "the timestamp update must not interrupt the user like an alert would");
        StringAssert.Contains(html, "aria-atomic", "the whole updated timestamp must be re-announced, not a partial diff");
    }

    [TestMethod]
    public async Task Canvas_UpdatedTimestampOnlyAnnouncesWhenValueActuallyChanges()
    {
        // Every render() rebuilds the "Updated" node from scratch, including
        // on the unconditional 5s poll. Without a change-detection guard, a
        // brand-new aria-live node would be inserted with identical text on
        // every poll tick and most screen readers would re-announce it
        // anyway -- reintroducing the exact announcement spam the narrowly
        // scoped live region (above) is meant to avoid.
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-live-guard", "credential-canvas-live-guard");
        using var client = AuthorizedClient("credential-canvas-live-guard");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "lastAnnouncedUpdate", "aria-live must only be attached when the announced timestamp text actually changed since the previous render");
    }

    [TestMethod]
    public async Task Canvas_RailKeyboardFocusIsRestoredAfterBackgroundPoll()
    {
        // render() unconditionally replaces every rail row (including the
        // .rail-select buttons the new arrow-key navigation targets) on
        // every 5s poll. Without restoring focus by Task ID after that
        // rebuild, a keyboard user's focus would silently fall back to
        // document.body the next time the background poll fires.
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-focus-guard", "credential-canvas-focus-guard");
        using var client = AuthorizedClient("credential-canvas-focus-guard");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "focusedRailTaskId", "rail keyboard focus must be captured/restored by Task ID across the background poll's full DOM rebuild");
        StringAssert.Contains(html, "select.dataset.railTaskId=member.taskId", "each rail-select button must expose its Task ID (under a key distinct from the delegated load handler's data-task-id) so focus can be restored to the equivalent row after a rebuild");
    }

    [TestMethod]
    public async Task Canvas_HonorsReducedMotionAndForcedColorsPreferences()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-motion", "credential-canvas-motion");
        using var client = AuthorizedClient("credential-canvas-motion");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "@media(prefers-reduced-motion:reduce)", "any future transition/animation must be suppressed for users who request reduced motion");
        StringAssert.Contains(html, "@media(forced-colors:active)", "selection and focus indicators must stay visible under a high-contrast/forced-colors theme");
    }

    [TestMethod]
    public async Task Canvas_RailClearAllPrecedesOptionsInSourceOrder()
    {
        var vault = CreateVault();
        await using var host = await StartHost(vault, "session-canvas-order", "credential-canvas-order");
        using var client = AuthorizedClient("credential-canvas-order");

        var response = await client.GetAsync($"{host.Url}/canvas?task_id=demo");
        var html = await response.Content.ReadAsStringAsync();

        // The rail header (with Clear all) is appended to the DOM before the
        // options list on every render, so a keyboard user tabbing through
        // the rail always reaches Clear all before any Task option -- proxy
        // for "focus order" since these tests cannot drive real Tab focus.
        var clearAllIndex = html.IndexOf("Clear all", StringComparison.Ordinal);
        var optionsListIndex = html.IndexOf("aria-label\",\"Loaded Tasks\"", StringComparison.Ordinal);

        Assert.AreNotEqual(-1, clearAllIndex, "Clear all control must be present");
        Assert.AreNotEqual(-1, optionsListIndex, "the rail options list must be present");
        Assert.IsLessThan(optionsListIndex, clearAllIndex, "Clear all must be constructed/appended before the options list so it precedes options in focus order");
    }
}
