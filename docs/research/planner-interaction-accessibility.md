# Planner interaction and accessibility research

**Access date:** 2026-08-17
**Method:** Primary Microsoft WinUI 3 and Windows App SDK guidance, plus settled
Glasswork Planner decisions and current repository contracts.

## Question

What platform constraints should shape the interaction, accessibility, responsive,
and recovery states of Glasswork's separate, today-only Planner Page?

## Settled Planner context

This research does not reopen the Planner decisions already recorded by the
Wayfinder map:

- Planner is a separate today-only Page. My Day remains intact.
- The selected visual direction is a split layout with capacity and scope
  decisions primary and a proportional, read-only day timeline secondary.
- All Actionable leaves initially count. **Not today** is the primary
  scope-reduction action and must be reversible.
- Capacity feedback is neutral: Headroom, At capacity, Over capacity, Uncertain,
  Unknown calendar, and Possibly stale.
- Unknown or incomplete Calendar Context cannot produce Available capacity,
  Open gaps, or a fit claim. A complete stale snapshot may retain those derived
  values with a Possibly stale qualifier.
- Explicit Size is Vault truth. Assumed size, Actionable-leaf expansion, fit,
  Open gaps, and tentative page interactions are derived and are never persisted.

Sources:

- [Issue #363: Planner product boundary](https://github.com/tjegbejimba/Glasswork/issues/363)
- [Issue #358: selected split layout](https://github.com/tjegbejimba/Glasswork/issues/358)
- [Issue #359: ADHD-tolerant capacity model](https://github.com/tjegbejimba/Glasswork/issues/359)
- [Issue #362: Planner domain model and Vault contract](https://github.com/tjegbejimba/Glasswork/issues/362)
- [Issue #366: availability and capacity algorithm](https://github.com/tjegbejimba/Glasswork/issues/366)
- [Issue #368: settings and local-data ownership](https://github.com/tjegbejimba/Glasswork/issues/368)

## Platform findings

### Keyboard interaction is a primary path

WinUI controls must be reachable through predictable tab traversal, and every
pointer action needs a keyboard equivalent. Built-in controls should carry the
interaction whenever possible: Button supplies Space/Enter activation and
ListView supplies arrow-key traversal. F6 and Shift+F6 pane traversal are not
automatic; applications with major panes must implement that cycle explicitly.

Planner implications:

- Wide-layout pane order is scope decisions, then day context.
- F6 cycles between the Planner navigation shell, scope region, and day-context
  region; Shift+F6 reverses that order.
- Tab follows visual and reading order inside each region.
- Arrow keys move among work rows; Tab enters a row's named actions.
- **Not today**, Undo, Refresh, Sign in, Retry, Reset, and disclosure controls
  must be ordinary focusable controls, not pointer-only hit zones.
- Responsive reflow must preserve the same semantic and focus order.

Source:
[Keyboard accessibility](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/keyboard-accessibility)

### Complex pages need landmarks and headings

Landmarks expose major regions and headings expose hierarchy within them, letting
assistive-technology users orient without traversing every control. Planner's
scope and day-context panes are distinct regions, and their visible titles should
also be semantic headings. The capacity summary belongs to the scope region,
because it explains the scope decisions rather than acting as an independent
pane.

Source:
[Landmarks and headings](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/landmarks-and-headings)

### Every action needs a contextual accessible name

WinUI's built-in controls expose role and state through UI Automation, but an
icon-only action still needs an explicit name. Names must include row context:
for example, "Move Review planner research out of today" rather than "Remove."
State descriptions should use full phrases rather than relying on visual
abbreviations such as `+45m`, `?`, or color.

Source:
[Accessibility overview](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview)

### Status announcements must be bounded

InfoBar is appropriate for a changed application state that should remain visible
without blocking work, including authentication failure, refresh failure, and
Possibly stale Calendar Context. It should not be used for every capacity
recalculation or as a transient confirmation for routine user actions. Updating
an already-open InfoBar does not notify screen readers; close and reopen it when
the changed message itself requires a new announcement.

Planner implications:

- Use persistent inline status for auth, setup, incomplete Calendar Context,
  stale data, and corrupt protected-store recovery.
- Keep the task-scope surface usable when only Calendar Context is unavailable.
- Announce one concise result after **Not today** or Undo, not every intermediate
  number that changed.
- Do not turn Over capacity or Uncertain into alarm-style error messaging; they
  are neutral fit states, not failures.

Source:
[InfoBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar)

### Contrast themes require semantic resources

Contrast themes are separate from light and dark themes. Custom Planner colors
must be defined through theme dictionaries and mapped to Windows SystemColor
resources for HighContrast. Meaning cannot depend on color alone. Fit states,
Tentative calendar spans, selected work, and Not today recovery therefore need
text, icons, patterns, or borders in addition to color.

Source:
[Contrast themes](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/high-contrast-themes)

### Responsive layout should reflow, not shrink

The XAML layout system supports fluid auto/star sizing and visual states that
reposition, reflow, show, hide, or replace parts of the interface. Planner should
preserve its information hierarchy when space narrows instead of compressing the
two panes until either becomes unusable.

Planner implications:

- Wide: independent scope and timeline regions.
- Narrow: one reading flow with scope first and day context after it.
- Text scaling and localization must be allowed to grow row height.
- A narrow layout must not introduce horizontal scrolling for work rows.

Source:
[Responsive layouts](https://learn.microsoft.com/en-us/windows/apps/develop/ui/layouts-with-xaml)

## State-model constraints for the prototype

| State | Required behavior |
| --- | --- |
| Normal | Capacity summary and Actionable leaves are primary; proportional calendar context is secondary and read-only. |
| Over capacity | Preserve every interaction; state the overage neutrally; make Not today visually primary. |
| Uncertain | Show the numeric comparison and identify the exact leaves causing uncertainty; never present a false fit claim. |
| Loading | Preserve page structure and the last complete snapshot when available; identify the region being refreshed without blocking unrelated task decisions. |
| Setup/auth failure | Suppress calendar-derived fit/Open gaps; retain Daily capacity and selected-work totals; provide one scoped recovery action. |
| Possibly stale | Keep the last complete qualified result, show snapshot age, and provide Refresh. |
| Unknown/incomplete calendar | Suppress fit and Open gaps rather than assuming an empty calendar. |
| Empty | Distinguish "no Actionable leaves" from "no calendar data"; show available capacity only when Calendar Context permits it. |
| Narrow | Reflow to scope first, day context second, while preserving semantic order. |
| High density | Use bounded independent scrolling at wide widths and one page flow when narrow; do not remove names or statuses to gain density. |
| Not today | Mutate through the existing My Day contract, announce the result, expose immediate Undo, and retain a page-session recovery path. |

## Verification implications

The eventual production slice should be checked with keyboard-only navigation,
Narrator, Accessibility Insights for Windows, text scaling, and each built-in
Windows contrast theme. The prototype should expose its full state and keyboard
map so these contracts can be judged before implementation.

Source:
[Accessibility testing](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-testing)
