---
name: tdd
description: Test-driven development and test selection for Glasswork. Use when implementing or fixing behavior, choosing a test surface, establishing RED/GREEN/REFACTOR, or deciding native and canvas verification.
---

# Test-driven development

Drive one observable behavior at a time through a public seam:

```text
RED -> minimal GREEN -> REFACTOR while green
```

Tests describe what a caller or user observes, not private structure. Place a
test with the behavior and seam it exercises. Reuse an existing behavior-focused
test class when it fits; create one when the seam needs a new home. Do not mirror
production classes mechanically.

## Keep the slice vertical

Start with one tracer bullet:

1. Choose one behavior and its public seam.
2. Write one test that fails meaningfully.
3. Add only enough implementation to pass it.
4. Refactor only while green, rerunning the narrow test after each material
   refactor.
5. Use what the slice taught you to choose the next behavior.

Writing all tests before any implementation is a horizontal slice. It commits to
imagined behavior and test structure before the real seam is understood.

## Prove a meaningful RED

RED means the intended test:

- is discovered and starts;
- reaches the selected public seam;
- fails because the requested behavior is absent or wrong; and
- reports an assertion or behavioral error that describes that gap.

These are setup failures, not RED:

- production or test code does not compile;
- restore, SDK, target-framework, or operating-system setup fails;
- the filter selects zero tests or discovery fails;
- required Canvas Host Debug output is missing or stale;
- a child process, native app, browser, or visual runner cannot start or capture;
- an unrelated test fails.

Repair the harness or prerequisite, then establish RED. A test that never
exercised the behavior provides no TDD evidence.

## Route the change

| Changed behavior | First RED | Prerequisite | Expand before completion |
|---|---|---|---|
| Core model, service, or domain policy | Matching behavior in `Glasswork.Tests` | Restore/build the current project | Relevant behavior/class, then the Core suite when shared Core behavior may be affected |
| MCP tool or transport | Matching behavior in `Glasswork.Mcp.Tests` | Restore/build MCP tests | MCP suite; add Core only when shared Core behavior changed |
| Canvas Host API, session, renderer, or browser contract | Matching behavior in `Glasswork.CanvasHost.Tests` | Build fresh Canvas Host Debug output before process-spawning tests | Relevant Canvas Host tests, then its suite; add Core only for shared Core changes |
| PowerShell script or workflow | Matching Pester file through `Invoke-ScriptTests.ps1` | The helper imports pinned Pester 5.7.1 | Relevant file during the loop; full script suite before completion |
| Native WinUI behavior with unchanged projection meaning | Behavior-specific native visual scenario | Windows and the real visual runner | Inspect the captured PNG; provide an explicit Windows handoff if the current agent cannot run it |
| Semantic Task Detail Projection behavior | Core projection test plus renderer-specific coverage | Canvas Host Debug build and native/canvas render environments | Core projection tests, Canvas Host tests and parity guard, actual native PNG, and actual browser-rendered Canvas Host PNG |
| Canvas-only interaction with unchanged projection meaning | Canvas/browser behavior at its own seam | Canvas prerequisites | Canvas evidence only; native parity is not required solely for a platform interaction |
| Change spanning several rows | Narrowest observable seam first | Each affected row's prerequisites | Union of affected surfaces, not every repository check by reflex |

## Tight command recipes

Use a fully qualified filter when duplicate method names exist. The first run
should restore normally. Add `--no-restore` only after a successful restore and
while project/dependency inputs are unchanged.

### Core

```powershell
dotnet test tests\Glasswork.Tests\Glasswork.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Glasswork.Tests.TaskDetailProjectionTests" `
  --nologo --verbosity minimal
```

### MCP

```powershell
dotnet test tests\Glasswork.Mcp.Tests\Glasswork.Mcp.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Glasswork.Mcp.Tests.QueryTasksToolTests" `
  --nologo --verbosity minimal
```

### Canvas Host

`Glasswork.CanvasHost.Tests` starts the real host from its Debug output even when
the test project runs in Release. Rebuild that output after host changes.

```powershell
dotnet build tools\Glasswork.CanvasHost\Glasswork.CanvasHost.csproj `
  --configuration Debug --nologo --verbosity minimal

dotnet test tests\Glasswork.CanvasHost.Tests\Glasswork.CanvasHost.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~Glasswork.CanvasHost.Tests.ProjectionParityGuardTests" `
  --nologo --verbosity minimal `
  --logger "trx;LogFileName=canvas-host.trx" `
  --results-directory TestResults\canvas-host
```

Canvas Host failure evidence and stable `GWCH_*` signatures are documented in
[`docs/agents/canvas-host-test-diagnostics.md`](../../docs/agents/canvas-host-test-diagnostics.md).

### PowerShell scripts

Target one file during the loop:

```powershell
pwsh -NoProfile -File scripts\Invoke-ScriptTests.ps1 `
  -TestPath tests\scripts\ReleaseWorkflow.Tests.ps1 `
  -ResultPath TestResults\pester\release-workflow.xml
```

Run the required full suite before completion:

```powershell
pwsh -NoProfile -File scripts\Invoke-ScriptTests.ps1 `
  -TestPath tests\scripts `
  -ResultPath TestResults\pester\script-tests.xml
```

The helper fails when it discovers no tests, skips tests, or cannot write its
NUnit XML result. Those outcomes are not behavioral RED.

## Expand minimally

During RED/GREEN, run only the selected behavior. After it passes:

1. Run the containing behavior/class or closest related group.
2. Run each affected test project.
3. Add required native and canvas visual evidence.
4. Let the protected CI checks provide the broader acceptance result.

Do not replace the tight loop with the full suite. Conversely, a narrow pass does
not erase a failed required acceptance run. Attempt 1 remains authoritative; an
isolated rerun is diagnostic evidence, not GREEN for the failed acceptance.

## Task Detail Projection parity

`TaskDetailProjection` is the semantic contract shared by native Task Detail and
the Canvas Host. Adding, removing, renaming, relabeling, reordering hierarchy, or
changing the meaning of a projection property requires both renderers in the
same change:

1. Drive the Core behavior through `TaskDetailProjectionTests` or equivalent
   behavior-focused projection coverage.
2. Update native Task Detail and run the applicable committed native scenario.
3. Update the Canvas Host and run its affected black-box tests plus
   `ProjectionParityGuardTests`.
4. Before merge, run the native scenario with `-MergeEvidence`, inspect the
   WinUI PNG, and retain `result.json` plus every capture.
5. Render the equivalent fixture through the real Canvas Host in a real browser,
   inspect the browser pixels, and retain the paired canvas PNG and inspection
   outcome.

Follow
[`visual-verification.md`](visual-verification.md#paired-task-detail-projection-evidence)
for the evidence contract. An HTTP response, serialized payload, HTML/source
assertion, or passing Canvas Host test is contract evidence, not a substitute
for either rendered PNG. Compare semantics and hierarchy; pixel equality is not
required.

A native-only shortcut or canvas-only ARIA/keyboard affordance does not trigger
the two-renderer rule when projection meaning is unchanged.

## Platform handoff

`Glasswork.App` and native visual verification require Windows. A cloud/Linux
agent must record the exact scenario and evidence still required; it must not
claim the native path passed or invent a Core test for UI-only behavior.

`Glasswork.Tests` targets `net10.0`, but a target framework or local override is
not Ubuntu proof. Report Linux verification only from an actual successful
Ubuntu run of the relevant Core/MCP checks. Canvas Host portability is not
implied by Core/MCP portability.

## Failure policy

There are no automatic Canvas Host retries. A known `GWCH_*` code changes
diagnostic routing, never pass/fail semantics. Any future retry requires the
issue, owner, expiry, and exact signature allowlist described by the diagnostics
policy; it may rerun one exact failed test for evidence, while the first failure
and job remain red.

Do not ignore named tests, add blanket exclusions, or call an opportunistic rerun
GREEN.

## Completion criteria

```text
[ ] Each slice began with an executed behavioral RED
[ ] Minimal implementation made that behavior green
[ ] Refactoring happened only while green
[ ] Filters discovered the intended tests
[ ] Canvas Host tests used fresh Debug output
[ ] Every affected test project passed its required acceptance run
[ ] Script changes passed the targeted and full Pester runs
[ ] Native changes have an inspected PNG or an explicit Windows handoff
[ ] Semantic projection changes have inspected native and browser-rendered canvas PNGs
[ ] No failed acceptance result was cleared by an isolated rerun
```
