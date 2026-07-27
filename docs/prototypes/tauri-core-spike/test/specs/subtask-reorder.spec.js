// PROTOTYPE ONLY -- Wayfinder ticket #372. Verifies the keyboard subtask
// reorder affordance (Alt+ArrowDown -- the accessibility alternative to
// HTML5 drag, ADR 0004) actually reorders subtasks end-to-end: real Tauri
// IPC call to `reorder_subtasks`, real re-render, and confirms the
// self-write coordinator doesn't let a subsequent file-watch echo clobber
// the optimistic update.
//
// NOTE ON APPROACH: WebDriverIO's `keys()` and the low-level Actions API
// `performActions()` key-chord (keyDown Alt + keyDown ArrowDown) do NOT
// reliably deliver a held-Alt-modifier arrow combo to the embedded WebKit
// driver on macOS -- confirmed by direct experimentation: both approaches
// left the subtask order completely unchanged, with no error. Dispatching
// a real `KeyboardEvent({ altKey: true, key: "ArrowDown" })` directly on
// the focused element via `browser.execute()` DOES exercise the exact same
// app code path (same event, same listener, same IPC call) and reorders
// correctly and stably. This is a WebDriver/WebKit key-delivery limitation,
// not an app bug -- documented here so the workaround isn't mistaken for
// masking real behavior. TJ should still confirm the physical Alt+ArrowDown
// keystroke (and native drag) work by hand during his HITL pass.
import fs from "fs";
import path from "path";

describe("subtask keyboard reorder (Alt+ArrowDown, ADR 0004)", () => {
  it("moves a subtask down via Alt+ArrowDown and persists the new order", async () => {
    const fixturePath = path.resolve("fixture-vault/budget-q3-review.md");
    const original = fs.readFileSync(fixturePath, "utf8");

    try {
      await $("[data-open-task='budget-q3-review']").click();
      await browser.pause(500);

      const orderBefore = await browser.execute(() =>
        Array.from(document.querySelectorAll(".subtask-row2 .subtext-btn")).map((el) => el.textContent.trim())
      );

      const grip = await $("[data-drag-handle='0']");
      await grip.click();
      await browser.pause(300);

      await browser.execute(() => {
        const el = document.activeElement;
        el.dispatchEvent(
          new KeyboardEvent("keydown", { key: "ArrowDown", altKey: true, bubbles: true, cancelable: true })
        );
      });

      await browser.waitUntil(
        async () => {
          const order = await browser.execute(() =>
            Array.from(document.querySelectorAll(".subtask-row2 .subtext-btn")).map((el) => el.textContent.trim())
          );
          return order[0] !== orderBefore[0];
        },
        { timeout: 4000, timeoutMsg: "Subtask order never changed after Alt+ArrowDown" }
      );

      // Give any file-watch echo (self-write coordinator round-trip) a moment
      // to land, then confirm the order is still correct -- i.e. the
      // optimistic IPC-driven update wasn't clobbered by a stale re-parse.
      await browser.pause(1000);

      const orderAfter = await browser.execute(() =>
        Array.from(document.querySelectorAll(".subtask-row2 .subtext-btn")).map((el) => el.textContent.trim())
      );

      expect(orderAfter[0]).toBe(orderBefore[1]);
      expect(orderAfter[1]).toBe(orderBefore[0]);
      expect(orderAfter.slice(2)).toEqual(orderBefore.slice(2));
    } finally {
      fs.writeFileSync(fixturePath, original, "utf8");
    }
  });
});
