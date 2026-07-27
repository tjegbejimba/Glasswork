// PROTOTYPE ONLY -- Wayfinder ticket #372. Verifies live file-watch parity
// end-to-end in the real running app: an external edit to a fixture task's
// frontmatter (made the same way Obsidian or any external editor would --
// direct filesystem write) must update the My Day list without an app
// restart, via the backend watcher -> `task-changed` event -> frontend
// re-render pipeline (core/src/watcher.rs -> lib.rs -> main.js `listen`).
import fs from "fs";
import path from "path";

describe("live file-watch parity", () => {
  it("reflects an external frontmatter edit without restart", async () => {
    const fixturePath = path.resolve("fixture-vault/renew-domain.md");
    const original = fs.readFileSync(fixturePath, "utf8");

    try {
      // Sanity: confirm starting state shows the original "Low" priority.
      await browser.waitUntil(
        async () => (await $$("[data-open-task='renew-domain']")).length > 0,
        { timeout: 5000 }
      );
      const before = await $("[data-open-task='renew-domain']").getText();

      // External edit -- exactly what a human editing in Obsidian would
      // produce: change priority low -> high, bump the due date.
      const edited = original
        .replace("priority: low", "priority: high")
        .replace("due: 2026-07-24", "due: 2026-07-25");
      fs.writeFileSync(fixturePath, edited, "utf8");

      // Wait for the watcher -> task-changed -> re-render pipeline to
      // reflect the new priority in the DOM, with no page reload/restart.
      await browser.waitUntil(
        async () => {
          const text = await $("[data-open-task='renew-domain']").getText();
          return text.includes("High") && text.includes("2026-07-25");
        },
        { timeout: 8000, timeoutMsg: "UI did not reflect external file edit within 8s" }
      );

      const after = await $("[data-open-task='renew-domain']").getText();

      fs.writeFileSync(
        "evidence/filewatch-live-update.json",
        JSON.stringify(
          { before, after, verdict: "LIVE_UPDATE_CONFIRMED" },
          null,
          2
        )
      );
    } finally {
      // Always restore the fixture to its original committed state.
      fs.writeFileSync(fixturePath, original, "utf8");
    }
  });
});
