// PROTOTYPE ONLY -- Wayfinder ticket #372. Verifies the HTML artifact
// sandbox boundary in the REAL Tauri WKWebView (not the browser debug
// harness), against the scorecard's Gate 2 acceptance script:
//
//   "the test HTML artifact contains a script that attempts one outbound
//    network request (e.g. fetch() to a canary URL) and one attempt at
//    parent-window/document access; open the preview and confirm via a
//    network monitor / browser dev tools that neither succeeds."
//
// An earlier version of this spec only checked that the parent title was
// unchanged and that `allow-scripts` was absent from the sandbox attribute.
// That is indirect: it infers the network probe failed rather than
// observing it. This version stands up a real local canary HTTP server and
// asserts zero requests arrive -- the deterministic equivalent of watching
// a network monitor.
//
// Critically it also fires a CONTROL request from the app's own (trusted)
// context first. Without that, "zero requests" is unfalsifiable: a canary
// server that was never reachable would look identical to a sandbox that
// held. The control proves the detector works before the negative result
// is allowed to mean anything.
import { expect } from "@wdio/globals";
import fs from "fs";
import http from "http";

const CANARY_PORT = 8787;

describe("html artifact sandbox boundary (real WKWebView)", () => {
  let server;
  let hits;

  before(async () => {
    hits = [];
    server = http.createServer((req, res) => {
      hits.push(req.url);
      res.writeHead(200, { "Access-Control-Allow-Origin": "*" });
      res.end("canary");
    });
    await new Promise((resolve) => server.listen(CANARY_PORT, "127.0.0.1", resolve));
  });

  after(async () => {
    if (server) await new Promise((resolve) => server.close(resolve));
  });

  it("blocks both the artifact's network probe and its parent-access probe", async () => {
    // ---- Control: prove the canary endpoint is reachable and counting.
    await browser.execute(async (port) => {
      try {
        await fetch(`http://127.0.0.1:${port}/canary?probe=control`);
      } catch (e) {
        /* recorded via server-side hit count either way */
      }
    }, CANARY_PORT);

    await browser.waitUntil(() => hits.some((u) => u.includes("probe=control")), {
      timeout: 5000,
      timeoutMsg:
        "Canary server never saw the control request -- the network detector itself is broken, so a zero-hit result from the sandboxed artifact would prove nothing.",
    });
    const controlHits = hits.filter((u) => u.includes("probe=control")).length;

    // ---- Now open the untrusted artifact's sandboxed preview.
    const titleBefore = await browser.getTitle();
    await browser.execute(() => {
      delete window.__sandboxBreached;
    });

    const back = await $$("[data-back-to-myday]");
    if (back.length > 0) await $("[data-back-to-myday]").click();
    await $("[data-open-task='budget-q3-review']").click();

    // ADR 0015: HTML artifacts open as Source, and the Preview is created
    // only on click. So the preview has to be genuinely *triggered* here --
    // this is the catalog interaction "trigger the HTML preview", not an
    // iframe that was already on screen.
    await $("[data-toggle-preview]").waitForExist({ timeout: 5000 });
    await expect(await $$(".html-preview-frame")).toBeElementsArrayOfSize(0);
    await $("[data-toggle-preview]").click();

    await browser.waitUntil(async () => (await $$(".html-preview-frame")).length > 0, {
      timeout: 5000,
      timeoutMsg: "sandboxed preview iframe never rendered after clicking Show preview",
    });

    // Give the (expected non-executing) script every chance to run.
    await browser.pause(1500);

    const artifactHits = hits.filter((u) => u.includes("probe=artifact")).length;
    const titleAfter = await browser.getTitle();
    const parentFlagSet = await browser.execute(() => window.__sandboxBreached === true);
    const sandboxAttr = await browser.execute(() => {
      const frame = document.querySelector(".html-preview-frame");
      return frame ? frame.getAttribute("sandbox") : null;
    });

    const networkProbeBlocked = artifactHits === 0;
    const parentProbeBlocked = titleAfter !== "BREACHED" && parentFlagSet !== true;

    const result = {
      gate: "Scorecard Phase 0 Gate 2 -- genuine HTML sandbox",
      method:
        "Local canary HTTP server on 127.0.0.1:8787 counts real inbound requests; a control request from the app's trusted context proves the detector works before the artifact's probe is judged.",
      control_request_hits: controlHits,
      artifact_network_probe_hits: artifactHits,
      network_probe_blocked: networkProbeBlocked,
      parent_title_before: titleBefore,
      parent_title_after: titleAfter,
      parent_flag_set: parentFlagSet === true,
      parent_probe_blocked: parentProbeBlocked,
      preview_is_opt_in: true,
      preview_default_state: "source view (ADR 0015: Source default, Preview created only on click)",
      sandbox_attr: sandboxAttr,
      allow_scripts_present: (sandboxAttr || "").includes("allow-scripts"),
      verdict:
        networkProbeBlocked && parentProbeBlocked && !(sandboxAttr || "").includes("allow-scripts")
          ? "SANDBOX_HELD"
          : "SANDBOX_BREACHED",
    };

    fs.writeFileSync(
      "evidence/html-sandbox-verification.json",
      JSON.stringify(result, null, 2) + "\n"
    );

    expect(controlHits).toBeGreaterThan(0);
    expect(networkProbeBlocked).toBe(true);
    expect(parentProbeBlocked).toBe(true);
    expect(result.verdict).toBe("SANDBOX_HELD");
  });
});
