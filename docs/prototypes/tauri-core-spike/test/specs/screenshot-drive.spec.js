// PROTOTYPE ONLY -- Wayfinder ticket #372. Drives the running app into
// specific states for external OS-level screenshot capture (screencapture),
// since macOS Automation/Accessibility permission for this ad-hoc-signed dev
// binary requires a fresh manual grant on every rebuild and isn't usable in
// a scripted loop. Long pauses give time to screencapture from outside this
// WebDriver session. Run with SCREENSHOT_STATE=<name> to pick which state.
describe("screenshot drive", () => {
  it("drives to the requested state and pauses", async () => {
    const targetState = process.env.SCREENSHOT_STATE || "myday-initial";

    if (targetState === "myday-initial") {
      // Default My Day view -- nothing to do.
    }

    if (targetState === "task-detail-blocked") {
      await $("[data-open-task='budget-q3-review']").click();
    }

    if (targetState === "html-preview") {
      await $("[data-open-task='budget-q3-review']").click();
      // Force the artifact fetch + render to complete.
      await browser.waitUntil(
        async () => (await $$(".html-preview-frame")).length > 0,
        { timeout: 5000 }
      );
      // Scroll the artifact iframe into view so the sandboxed preview is
      // visible in the (short) window before the external screencapture.
      await browser.execute(() => {
        document.querySelector(".html-preview-frame")?.scrollIntoView({ block: "end" });
      });
    }

    if (targetState === "planner-stub") {
      await $("[data-page='planner']").click();
    }

    if (targetState === "expanded-subtask-blocker") {
      await $("[data-open-task='budget-q3-review']").click();
      await $("[data-expand-subtask='2']").click(); // "Get sign-off from manager" (blocked)
    }

    await browser.pause(60000);
  });
});
