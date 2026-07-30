// PROTOTYPE ONLY -- Wayfinder ticket #372. Regression test for the untrusted
// Vault-content boundary.
//
// CONTEXT.md and ADR 0006 treat everything in the Vault as untrusted: task
// prose is largely agent-authored, so a Task's Description/Notes/title can
// contain anything. This spike originally interpolated those straight into
// `innerHTML`, which executes markup. These tests pin the fix.
//
// The payload deliberately uses `<img onerror>` rather than `<script>`:
// HTML5 does *not* execute <script> inserted via innerHTML, so a <script>
// payload would pass even against vulnerable code and prove nothing. An
// `onerror` handler on a broken image *does* fire, so it is a real detector.
//
// The fixed 3-task fixture (#370) must stay byte-identical, so each test
// snapshots the file it poisons and restores it in `finally`.
import { expect } from "@wdio/globals";
import fs from "fs";
import path from "path";

const FIXTURE = path.resolve("fixture-vault/budget-q3-review.md");

const IMG_PAYLOAD = `<img src=x onerror="window.__xssDescription = true">`;
const NOTES_PAYLOAD = `<img src=x onerror="window.__xssNotes = true">`;

async function openDetailFor(taskId) {
  // Task Detail is now a distinct Page, so the app does not reset to My Day
  // between tests -- navigate back first if a previous test left us there.
  const back = await $$("[data-back-to-myday]");
  if (back.length > 0) {
    await $("[data-back-to-myday]").click();
  }
  await browser.waitUntil(
    async () => (await $$(`[data-open-task='${taskId}']`)).length > 0,
    { timeout: 8000, timeoutMsg: `task row ${taskId} never appeared` }
  );
  await $(`[data-open-task='${taskId}']`).click();
  await $("h3=Description").waitForExist({ timeout: 5000 });
}

describe("untrusted Vault content boundary", () => {
  beforeEach(async () => {
    await browser.execute(() => {
      delete window.__xssDescription;
      delete window.__xssNotes;
    });
  });

  it("does not execute markup embedded in a Task's Description", async () => {
    const original = fs.readFileSync(FIXTURE, "utf8");
    try {
      fs.writeFileSync(
        FIXTURE,
        original.replace("## Subtasks", `${IMG_PAYLOAD}\n\n## Subtasks`),
        "utf8"
      );

      // Wait for the watcher -> re-render pipeline to pick the poison up.
      await browser.pause(1200);
      await openDetailFor("budget-q3-review");

      const fired = await browser.execute(() => window.__xssDescription === true);
      expect(fired).toBe(false);

      // The injected markup must not have become a live element either --
      // "didn't fire yet" is not the same as "wasn't parsed as HTML".
      const injectedImages = await $$("img[src='x']");
      expect(injectedImages).toBeElementsArrayOfSize(0);
    } finally {
      fs.writeFileSync(FIXTURE, original, "utf8");
    }
  });

  it("renders the markup as literal visible text instead of swallowing it", async () => {
    const original = fs.readFileSync(FIXTURE, "utf8");
    try {
      fs.writeFileSync(
        FIXTURE,
        original.replace("## Subtasks", `${IMG_PAYLOAD}\n\n## Subtasks`),
        "utf8"
      );
      await browser.pause(1200);
      await openDetailFor("budget-q3-review");

      // Escaping, not stripping: the user should still see what the agent
      // wrote, so a hostile payload is visible rather than silently dropped.
      const detailText = await $(".detail-panel").getText();
      expect(detailText).toContain("<img");
      expect(detailText).toContain("onerror");
    } finally {
      fs.writeFileSync(FIXTURE, original, "utf8");
    }
  });

  it("does not execute markup embedded in a Task's Notes", async () => {
    const original = fs.readFileSync(FIXTURE, "utf8");
    try {
      fs.writeFileSync(
        FIXTURE,
        original.replace("## Notes\n", `## Notes\n\n${NOTES_PAYLOAD}\n`),
        "utf8"
      );
      await browser.pause(1200);
      await openDetailFor("budget-q3-review");

      const fired = await browser.execute(() => window.__xssNotes === true);
      expect(fired).toBe(false);
    } finally {
      fs.writeFileSync(FIXTURE, original, "utf8");
    }
  });
});
