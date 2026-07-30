// PROTOTYPE ONLY -- Wayfinder ticket #372. Verifies live file-watch parity
// end-to-end in the real running app: an external edit to a fixture task's
// frontmatter (made the same way Obsidian or any external editor would --
// direct filesystem write) must update the My Day list without an app
// restart, via the backend watcher -> `task-changed` event -> frontend
// re-render pipeline (core/src/watcher.rs -> lib.rs -> main.js `listen`).
//
// The task under test is fixed by the spec, not chosen freely: #370 names
// "Confirm Tailscale ACL update" as the fixture task that exists "purely to
// demonstrate live file-watch", and the scorecard's metric 5 measures
// file-watch response against that same row. An earlier version of this
// spec exercised `renew-domain` instead, which did prove the pipeline but
// did not match the locked contract.
import { expect } from "@wdio/globals";
import fs from "fs";
import path from "path";

const FIXTURE_ID = "confirm-tailscale-acl";
const FIXTURE = path.resolve(`fixture-vault/${FIXTURE_ID}.md`);

describe("live file-watch parity", () => {
  it("reflects an external frontmatter edit to the Tailscale ACL task without restart", async () => {
    const original = fs.readFileSync(FIXTURE, "utf8");

    try {
      // Make sure we're on My Day (Task Detail is its own Page now).
      const back = await $$("[data-back-to-myday]");
      if (back.length > 0) await $("[data-back-to-myday]").click();

      await browser.waitUntil(
        async () => (await $$(`[data-open-task='${FIXTURE_ID}']`)).length > 0,
        { timeout: 8000, timeoutMsg: "Tailscale ACL row never rendered" }
      );
      const before = await $(`[data-open-task='${FIXTURE_ID}']`).getText();
      expect(before).toContain("Med");

      // External edit -- exactly what a human editing in Obsidian would
      // produce: raise priority, bump the due date.
      const edited = original
        .replace("priority: medium", "priority: high")
        .replace("due: 2026-07-24", "due: 2026-07-25");
      const editedAtMs = Date.now();
      fs.writeFileSync(FIXTURE, edited, "utf8");

      await browser.waitUntil(
        async () => {
          const text = await $(`[data-open-task='${FIXTURE_ID}']`).getText();
          return text.includes("High") && text.includes("2026-07-25");
        },
        { timeout: 8000, timeoutMsg: "UI did not reflect external file edit within 8s" }
      );
      const observedAtMs = Date.now();

      const after = await $(`[data-open-task='${FIXTURE_ID}']`).getText();
      expect(after).toContain("High");
      expect(after).toContain("2026-07-25");

      // Coarse wall-clock bound on the watch->render pipeline. This is NOT
      // the scorecard's metric-5 value: that requires a 3-run median
      // timestamped off the acceptance recording, which is still pending
      // (see README "Measured evidence"). Recorded here only as supporting
      // evidence that the pipeline is live rather than a reload in disguise.
      const elapsedMs = observedAtMs - editedAtMs;
      fs.writeFileSync(
        path.resolve("evidence/filewatch-live-update.json"),
        JSON.stringify(
          {
            fixture_task: FIXTURE_ID,
            fixture_task_title: "Confirm Tailscale ACL update",
            before,
            after,
            observed_round_trip_ms: elapsedMs,
            observed_round_trip_caveat:
              "Single observation, WebDriver-polled wall clock. NOT the scorecard metric-5 value (3-run median timestamped from the acceptance recording), which remains pending.",
            verdict: "LIVE_UPDATE_CONFIRMED",
          },
          null,
          2
        ) + "\n",
        "utf8"
      );
    } finally {
      fs.writeFileSync(FIXTURE, original, "utf8");
    }
  });
});
