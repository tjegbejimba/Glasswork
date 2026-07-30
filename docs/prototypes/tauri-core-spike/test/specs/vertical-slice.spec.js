// PROTOTYPE ONLY -- Wayfinder ticket #372. Automated desktop UI test
// satisfying the scorecard's hard gate #5 ("a real automated test passes,
// exercising a real fixture interaction, not just an app-launch smoke test")
// and contributing to the "Testability breadth" measured metric (#376):
// toggle a subtask, open Task Detail, and navigate via keyboard are each
// covered as distinct catalog interactions.
import { expect } from "@wdio/globals";
import fs from "fs";
import path from "path";

// Task Detail is a real Page now, so specs no longer implicitly start on
// My Day -- navigate back explicitly where a test needs the list.
async function goToMyDay() {
  const back = await $$("[data-back-to-myday]");
  if (back.length > 0) await $("[data-back-to-myday]").click();
  await $("h2=My Day").waitForExist({ timeout: 5000 });
}

describe("Glasswork Tauri spike -- shared vertical slice", () => {
  it("loads My Day with the fixed 3-task fixture", async () => {
    await browser.pause(1000); // allow Rust Core vault load + first render
    const heading = await $("h2=My Day");
    await expect(heading).toExist();

    const rows = await $$(".task-row");
    await expect(rows).toBeElementsArrayOfSize(3);
  });

  it("opens Task Detail for the rich card task on click (real interaction #1)", async () => {
    const cardTitle = await $("[data-open-task='budget-q3-review']");
    await cardTitle.click();

    const description = await $("h3=Description");
    await expect(description).toExist();

    const subtaskHeading = await $("h3*=Subtasks");
    await expect(subtaskHeading).toExist();
  });

  it("toggles a subtask's done state via the circle hit-zone (real interaction #2, ADR 0004)", async () => {
    // This mutates the fixture's persisted status (round-trips through the
    // bounded Rust Core vault writer) -- snapshot + restore so the fixed
    // 3-task fixture stays byte-identical to its committed state for every
    // other spec/screenshot/recording that depends on it.
    const fixturePath = path.resolve("fixture-vault/budget-q3-review.md");
    const original = fs.readFileSync(fixturePath, "utf8");
    try {
      const beforePressed = await $("button[data-toggle-subtask='1']").getAttribute("aria-pressed");
      await $("button[data-toggle-subtask='1']").click();

      // Re-query fresh each poll: main.js fully re-renders #main-content on
      // every state change, so the original WebElement handle goes stale.
      await browser.waitUntil(
        async () => (await $("button[data-toggle-subtask='1']").getAttribute("aria-pressed")) !== beforePressed,
        { timeout: 5000, timeoutMsg: "toggled subtask did not flip aria-pressed after round-tripping through the bounded Rust Core" }
      );
    } finally {
      fs.writeFileSync(fixturePath, original, "utf8");
    }
  });

  it("keeps Planner a reserved, non-routable nav entry with zero content (real interaction #3)", async () => {
    // #370: the Planner nav item exists so the framework proves it can host
    // another destination later, but "ships no Planner content, not even a
    // static layout". So the entry must be present and reachable, yet must
    // not navigate anywhere -- not even to text explaining that it's
    // reserved, which is itself Planner Page content.
    await goToMyDay();
    const plannerNavItem = await $("[data-page='planner']");
    await expect(plannerNavItem).toExist();
    await expect(plannerNavItem).toHaveAttribute("aria-disabled", "true");

    await plannerNavItem.click();

    // Still on My Day -- the click did not route.
    const myDayHeading = await $("h2=My Day");
    await expect(myDayHeading).toExist();
    const plannerHeading = await $$("h2=Planner");
    await expect(plannerHeading).toBeElementsArrayOfSize(0);

    // Reachable by keyboard focus (accessibility gate 4 requires every zone
    // to be focus-reachable; aria-disabled keeps it announced-but-inert
    // rather than removing it from the tab order the way `disabled` would).
    const isFocusable = await browser.execute(() => {
      const el = document.querySelector("[data-page='planner']");
      el.focus();
      return document.activeElement === el;
    });
    await expect(isFocusable).toBe(true);
  });

  it("opens Task Detail as its own Page and navigates back to My Day", async () => {
    // #370's three-page shell: Task Detail is a distinct destination, not an
    // inline expansion of the My Day row.
    await goToMyDay();
    await $("[data-open-task='budget-q3-review']").click();

    await expect(await $("h3=Description")).toExist();
    // My Day's list is gone -- we navigated, not expanded.
    await expect(await $$("h2=My Day")).toBeElementsArrayOfSize(0);

    await $("[data-back-to-myday]").click();
    await expect(await $("h2=My Day")).toExist();
    await expect(await $$("h3=Description")).toBeElementsArrayOfSize(0);
  });
});
