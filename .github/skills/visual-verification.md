---
name: visual-verification
description: Verify Glasswork app (WinUI 3) changes by rendering the real UI, and author/scaffold verification scenarios by inspecting the live UI Automation tree. Use when changing anything under src/Glasswork.App (XAML, code-behind, ViewModels, App.xaml.cs, draw-affecting services), when a screenshot/visual check is needed, or when writing/discovering AutomationId selectors for a verification scenario.
---

# Visual verification & scenario authoring

The MSTest suite (`Glasswork.Core`) cannot catch XAML parse errors, layout
regressions, blank screens, or the silent `STOWED_EXCEPTION 0xc000027b` crashes
that hard rule 6 describes. Only rendering the real WinUI app can. This skill
covers (1) verifying a change visually and (2) authoring the deterministic
scenarios that make verification repeatable — including a "computer use"
authoring aid that introspects the live UI Automation tree.

> Relationship to the hard rules: this skill is the *how* behind
> copilot-instructions.md hard rules 6 & 7. Those rules still stand; this skill
> is the playbook for satisfying them.

**Windows + local only.** Everything here builds the Debug WinUI app and drives
a real window. Cloud/Linux agents cannot run it — they must flag UI-touching
work in the PR description for local re-verification before merge.

---

## 1. Verify a change

All scripts launch the Debug build with a verification-only AppInstance key and
an isolated temp Vault + UI-state file, skipping protocol registration and
update checks. They never touch the user's installed Glasswork, real Vault,
normal UI state, or the `glasswork://` handler. Only the spawned dev-build PID
is screenshotted and killed.

- **Generic startup smoke test** (no scenario needed):

  ```powershell
  pwsh -File scripts\verify-app.ps1
  ```

  Builds, launches, waits for the window, screenshots, kills the PID, prints the
  PNG path. **View the PNG** before declaring the task done. If the process
  exits before showing a window, that's a startup crash — check Event Viewer >
  Application for `STOWED_EXCEPTION` / `XamlParseException` (hard rule 6).

- **Behavior-specific verification** (preferred): run a JSON scenario that seeds
  a deterministic Vault, navigates, and captures named PNGs. The runner rejects
  blank/uniform screenshots automatically.

  ```powershell
  pwsh -File scripts\invoke-visual-verification.ps1 -Scenario scripts\visual-verification\backlog-smoke.json
  ```

  **Add or update a scenario that covers the behavior you changed**, then view
  the captured PNG(s) under the printed output directory.

---

## 2. Scenario schema (cheat-sheet)

Scenarios live in `scripts\visual-verification\*.json` and deserialize via
`VisualVerificationScenario` (`Glasswork.Core`). camelCase, comments + trailing
commas allowed.

```jsonc
{
  "name": "Backlog smoke",            // required, non-empty
  "startUri": "glasswork://backlog",  // optional deep link passed as argv[0]
  "theme": "dark",                    // optional: system (default), light, dark
  "launchTimeoutSeconds": 20,
  "initialWaitMilliseconds": 800,
  "tasks": [                          // seeded into the isolated Vault
    { "id": "safe-slug", "title": "...", "status": "todo", "priority": "high",
      "subtasks": [ { "text": "...", "status": "in_progress" } ],
      "artifacts": [ { "name": "plan.md", "markdown": "# ..." } ] }
  ],
  "wikiPages": [                      // seeded under Vault/wiki/
    { "relativePath": "concepts/example.md", "id": "example",
      "title": "Example", "type": "concept", "confidence": "high",
      "updated": "2026-08-15", "expires": "2027-01-01",
      "researchRelatedWork": ["safe-slug"],
      "markdown": "# Example\n\nSynthesis." }
  ],
  "actions": [                        // UI Automation driven, in order
    { "type": "select",     "automationId": "NavBacklog", "timeoutMilliseconds": 10000 },
    { "type": "wait-for",   "automationId": "BacklogHeader" },
    { "type": "set-value",  "automationId": "BacklogSearchBox", "value": "design" },
    { "type": "invoke",     "name": "Some button" }
  ],
  "captures": [ { "name": "backlog", "waitMilliseconds": 0 } ]  // >= 1 required
}
```

Action types: `wait-for` (asserts an element appears), `select`
(SelectionItem/Invoke), `invoke` (InvokePattern), `set-value` (ValuePattern),
`focus` (sets and verifies keyboard focus), and `assert-single-selection`
(verifies the target exposes exactly one accessible selection and disallows
multiple selection). `assert-clipboard-text` compares the clipboard's plain text
with `value`, which verifies copy-driven launch contracts without exposing them
in the UI.
Target by `automationId` (preferred — stable) or `name`.

---

## 3. Discover selectors

Actions need real `AutomationId`s. Two ways to find them:

- **Grep the XAML** for what's already tagged:

  ```
  AutomationProperties.AutomationId
  ```

  Convention: PascalCase ids; landmark headers end with `Header` (e.g.
  `NavBacklog`, `BacklogHeader`, `BacklogTaskList`, `MyDayTodayHeader`).

- **Inspect the live tree** (the authoring aid — see §4) when you don't know the
  ids, the screen is new, or you want the full actionable menu.

If the control you need has no `AutomationId`, add one in the XAML (it's also an
accessibility win) rather than targeting by a brittle `name`.

---

## 4. Author scenarios with the inspection aid ("computer use")

`scripts\inspect-app.ps1` is the authoring aid: it gives an agent the two things
a computer-use loop needs — **eyes** (a screenshot) and the **accessibility
tree** (selectors) — then scaffolds a starter scenario. It keeps CI
deterministic because the committed scenario is hand-finalized, not model-driven.

**Workflow: seed → inspect → refine.**

1. Write a tiny *seed* scenario — just enough to reach the screen you care about
   (a `startUri`, optional nav `select`/`wait-for` actions, and one throwaway
   `capture` since the schema requires one).
2. Run the aid:

   ```powershell
   pwsh -File scripts\inspect-app.ps1 -Scenario scripts\visual-verification\my-seed.json -OutDir out\inspect
   ```

3. Read the three artifacts it writes into the output dir:
   - **`inspection.png`** — screenshot captured at the same moment as the tree
     walk, so the catalog lines up with the pixels.
   - **`inspection.json`** — the selector catalog (see shape below).
   - **`scenario.suggested.json`** — a minimal, valid starter scenario
     (waits for up to two stable landmark anchors + one capture). Use it as a
     base; it deliberately omits state-mutating actions.
4. Pick the actions you need from the catalog's `candidates` and flesh out a
   real scenario, then commit it under `scripts\visual-verification\` and run it
   through `invoke-visual-verification.ps1`.

### `inspection.json` shape

```jsonc
{
  "schemaVersion": 1,
  "screenName": "...", "startUri": "...", "windowTitle": "Glasswork",
  "screenshotFile": "inspection.png",      // the paired PNG
  "windowBounds": { "x": .., "y": .., "width": .., "height": .. },  // screen px
  "dpiScale": 1,
  "warnings": [ "N UI element(s) were skipped due to UI Automation errors." ],
  "elements": [                            // bounds are SCREENSHOT-relative px
    { "automationId": "BacklogHeader", "name": "Backlog", "controlType": "Text",
      "depth": 4, "isOffscreen": false, "isEnabled": true,
      "patterns": ["..."], "bounds": { "x": .., "y": .., "width": .., "height": .. } }
  ],
  "candidates": {                          // ready-to-use, grouped by action
    "invokable":   [ /* -> "invoke"    actions (buttons) */ ],
    "selectable":  [ /* -> "select"    actions (nav items, list items) */ ],
    "valueFields": [ /* -> "set-value" actions (text boxes) */ ],
    "toggles":     [ /* toggle controls */ ]
  }
}
```

`candidates.selectable` → `select` actions, `valueFields` → `set-value`,
`invokable` → `invoke`. Element `bounds` are relative to the window's top-left
(matching `inspection.png`), so a vision model can map a catalog entry to pixels.

### Limitations of the aid

- Walks the **ControlView** tree of the main window. WinUI flyouts/menus that
  live in a separate popup HWND may not appear; drive the app to the state you
  want (e.g. open the dialog via an action) before inspecting, and if it's a
  separate window, target it with its own seed.
- Per-element UIA reads are defensive (failures are skipped and counted in
  `warnings`), so a noisy/transient tree degrades gracefully rather than failing.
- The scaffolded scenario is a *starting point*, not a finished test.

---

## Checklist

```
[ ] Change touches src\Glasswork.App\ -> visual verification is required
[ ] Ran verify-app.ps1 or a scenario; VIEWED the PNG (not just exit code)
[ ] New/changed behavior has a committed scenario under scripts\visual-verification\
[ ] New selectors discovered via grep or inspect-app.ps1; added AutomationIds where missing
[ ] Cloud/Linux: flagged UI work for local re-verification in the PR description
```
