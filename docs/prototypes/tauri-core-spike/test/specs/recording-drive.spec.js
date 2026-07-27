// PROTOTYPE ONLY -- Wayfinder ticket #372. Drives a short, deterministic
// sequence for external screen-recording capture: opens Task Detail,
// performs a keyboard subtask reorder (Alt+ArrowDown -- accessibility
// alternative to HTML5 drag), then makes an EXTERNAL frontmatter edit to a
// different fixture task and returns to My Day to show the live update
// land without an app restart. Run alongside `screencapture -v -V<secs>`.
import fs from "fs";
import path from "path";

describe("recording drive", () => {
  it("performs reorder then live file-watch update, with pauses for recording", async () => {
    const fixturePath = path.resolve("fixture-vault/confirm-tailscale-acl.md");
    const original = fs.readFileSync(fixturePath, "utf8");
    // The reorder step below also mutates and persists budget-q3-review.md
    // (round-trips through the bounded Rust Core vault writer) -- snapshot
    // and restore it too, same as confirm-tailscale-acl.md.
    const reorderedFixturePath = path.resolve("fixture-vault/budget-q3-review.md");
    const reorderedOriginal = fs.readFileSync(reorderedFixturePath, "utf8");
    const gateFile = path.resolve("/tmp/recording-drive-go.signal");

    try {
      // Starting gate: block here (app already launched + visible) until an
      // external controller drops a signal file, once it has confirmed the
      // window is frontmost and `screencapture -v` is actively recording.
      await browser.waitUntil(() => fs.existsSync(gateFile), {
        timeout: 30000,
        interval: 200,
        timeoutMsg: "Timed out waiting for external recording-start signal",
      });

      await $("[data-open-task='budget-q3-review']").click();
      await browser.pause(1500);

      // Keyboard reorder: focus the first subtask's grip, then a single
      // Alt+ArrowDown moves "Collect Q2 actuals" down one position, past
      // "Reconcile NAS hosting costs".
      //
      // NOTE: WebDriverIO's keys()/performActions() key-chord does not
      // reliably deliver a held-Alt + ArrowDown combo to the embedded
      // WebKit driver on macOS (confirmed by direct experimentation -- see
      // subtask-reorder.spec.js header comment). Dispatching a real
      // KeyboardEvent via browser.execute() exercises the identical app
      // code path and reorders correctly and visibly for the recording.
      //
      // Only ONE press is used here (not two) -- a documented finding from
      // this evidence pass: `restoreFocus()` re-focuses by screen POSITION
      // (data-drag-handle index), not by subtask identity, so a second
      // consecutive Alt+ArrowDown without an intervening Tab/refocus just
      // swaps the same two adjacent rows back, rather than continuing to
      // move the same subtask further down. Worth a HITL note to TJ; not a
      // gate blocker for this ticket, so not fixed here (disposable spike).
      const grip = await $("[data-drag-handle='0']");
      await grip.click();
      await browser.pause(500);
      await browser.execute(() => {
        document
          .activeElement
          .dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", altKey: true, bubbles: true, cancelable: true }));
      });
      await browser.pause(1800);

      // External edit to a DIFFERENT task file, simulating an Obsidian /
      // other-editor change while the app keeps running.
      const edited = original
        .replace("status: todo", "status: done")
        .replace("priority: medium", "priority: low");
      fs.writeFileSync(fixturePath, edited, "utf8");

      // Back to My Day to show the live-updated row.
      await $("[data-page='myday']").click();
      await browser.waitUntil(
        async () => {
          const text = await $("[data-open-task='confirm-tailscale-acl']").getText();
          return text.includes("Low");
        },
        { timeout: 6000 }
      );
      await browser.pause(2000);
    } finally {
      fs.writeFileSync(fixturePath, original, "utf8");
      fs.writeFileSync(reorderedFixturePath, reorderedOriginal, "utf8");
    }
  });
});
