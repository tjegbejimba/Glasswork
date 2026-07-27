// PROTOTYPE ONLY -- Wayfinder ticket #372. Single-shot performance capture
// spec: run this file fresh (one `wdio run` invocation = one process launch)
// three times via scripts/run-perf-measurements.sh to get a 3-run sample,
// per the scorecard's (#376) measurement procedure. Reads in-page
// performance.now() timestamps instrumented in src/main.js -- avoids
// needing macOS Accessibility/Automation permission just to time a render.
import fs from "node:fs";

describe("Glasswork Tauri spike -- performance capture", () => {
  it("captures cold-launch render time", async () => {
    await browser.waitUntil(
      async () => await browser.execute(() => typeof window.__myDayRenderedAtMs === "number"),
      { timeout: 15000, timeoutMsg: "My Day never rendered within timeout" }
    );
    const coldLaunchMs = await browser.execute(() => window.__myDayRenderedAtMs);
    appendResult({ metric: "cold_launch_ms", value: coldLaunchMs });
  });

  it("captures task-detail interaction latency", async () => {
    const cardTitle = await $("[data-open-task='budget-q3-review']");
    await cardTitle.click();

    await browser.waitUntil(
      async () => await browser.execute(() => typeof window.__lastInteractionLatencyMs === "number"),
      { timeout: 10000, timeoutMsg: "interaction latency was never recorded" }
    );
    const latencyMs = await browser.execute(() => window.__lastInteractionLatencyMs);
    appendResult({ metric: "interaction_latency_ms", value: latencyMs });
  });
});

function appendResult(entry) {
  const path = "./evidence/measured-performance-macos-raw.jsonl";
  fs.appendFileSync(path, JSON.stringify(entry) + "\n");
}
