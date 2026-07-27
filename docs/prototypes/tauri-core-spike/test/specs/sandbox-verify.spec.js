// PROTOTYPE ONLY -- Wayfinder ticket #372. Verifies the HTML artifact
// sandbox boundary in the REAL Tauri WKWebView (not the browser debug
// harness). report.html's canary script attempts two breakout probes:
// an outbound fetch, and a parent-window/document mutation. Since the
// iframe uses sandbox="allow-same-origin" with NO "allow-scripts" token,
// the <script> block should never execute at all -- the strongest
// possible sandboxing (script execution disabled entirely, not merely
// network-restricted).
import fs from "fs";

describe("html artifact sandbox boundary (real WKWebView)", () => {
  it("blocks the untrusted artifact's breakout probes", async () => {
    const titleBefore = await browser.getTitle();

    await $("[data-open-task='budget-q3-review']").click();
    await browser.waitUntil(
      async () => (await $$(".html-preview-frame")).length > 0,
      { timeout: 5000 }
    );

    // Give the (non-executing) script every chance to have run if the
    // sandbox were broken.
    await browser.pause(1000);

    const titleAfter = await browser.getTitle();
    const breakoutSucceeded = titleAfter === "BREACHED";

    // Inspect the iframe's sandbox attribute directly from the DOM to
    // confirm allow-scripts is absent (the actual enforcement mechanism).
    const sandboxAttr = await browser.execute(() => {
      const frame = document.querySelector(".html-preview-frame");
      return frame ? frame.getAttribute("sandbox") : null;
    });

    const result = {
      titleBefore,
      titleAfter,
      breakoutSucceeded,
      sandboxAttr,
      allowScriptsPresent: (sandboxAttr || "").includes("allow-scripts"),
      verdict:
        !breakoutSucceeded && !(sandboxAttr || "").includes("allow-scripts")
          ? "SANDBOX_HELD"
          : "SANDBOX_BREACHED",
    };

    fs.writeFileSync(
      "evidence/html-sandbox-verification.json",
      JSON.stringify(result, null, 2)
    );

    if (result.verdict !== "SANDBOX_HELD") {
      throw new Error("Sandbox boundary did not hold: " + JSON.stringify(result));
    }
  });
});
