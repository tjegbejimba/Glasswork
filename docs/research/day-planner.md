# Research: A Native Day/Workweek Planner for Glasswork

**Status:** Research only; no product implementation.

**Question:** Should Glasswork add an hourly planner showing Microsoft 365
meetings alongside Glasswork Tasks and Subtasks, so users can place work into
available time instead of overcommitting the current list-based My Day?

## Problem framing

My Day identifies work that is in scope today, but it has no concept of
time-of-day or capacity. A user can select more work than the open time between
meetings can hold, without receiving any indication that the plan is
unrealistic.

The user's stated problem is therefore capacity and overcommitment. An hourly
calendar may help visualize that problem, but it should not be assumed to be
the smallest or best solution.

## Microsoft and WinUI feasibility

### Teams cannot be embedded as a native calendar

Microsoft's Teams extensibility model embeds third-party web content *inside*
Teams through tabs, personal apps, and meeting panels. Microsoft does not offer
an embeddable Teams calendar control for arbitrary WinUI applications.

Sources:

- [Teams apps in meetings overview](https://learn.microsoft.com/en-us/microsoftteams/platform/apps-in-teams-meetings/teams-apps-in-meetings-overview)
- [Build tabs for meetings](https://learn.microsoft.com/en-us/microsoftteams/platform/apps-in-teams-meetings/build-tabs-for-meeting)

### Microsoft 365 events can be rendered in a custom native planner

The supported approach is to retrieve Outlook/Microsoft 365 calendar data
through Microsoft Graph and render it in a Glasswork-owned UI:

- `GET /me/calendar/calendarView` returns event occurrences within a date
  range, including expanded recurring-event instances.
- `Calendars.ReadBasic` is the least-privileged delegated permission for basic
  event data; richer event details require `Calendars.Read`.
- The request uses ISO 8601 date-time offsets, while the response time zone can
  be selected with `Prefer: outlook.timezone`.
- `calendarView/delta` supports incremental synchronization, but polling the
  visible day or week is simpler for an initial single-user implementation.
- Webhooks would require a public HTTPS endpoint and are disproportionate for a
  desktop-only reader.

Sources:

- [List calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0&tabs=http)
- [Recurring events](https://learn.microsoft.com/en-us/graph/outlook-schedule-recurring-events)
- [Event delta query](https://learn.microsoft.com/en-us/graph/api/event-delta?view=graph-rest-1.0)
- [Change notifications](https://learn.microsoft.com/en-us/graph/change-notifications-overview)
- [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)

For authentication, Glasswork would be registered as a public desktop client
without a client secret. MSAL.NET can use Windows Web Account Manager for the
native account picker, silent SSO, Windows Hello, and Conditional Access, with
a browser fallback.

Sources:

- [Configure a desktop app](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration)
- [Acquire tokens with WAM](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)

TaskNotes provides a close real-world precedent: a single-user, vault-based
desktop application using Microsoft delegated calendar permissions and a
public-client OAuth flow.

- [TaskNotes calendar setup](https://tasknotes.dev/calendar-setup/)
- [TaskNotes calendar setup source](https://raw.githubusercontent.com/callumalpass/tasknotes/main/docs/calendar-setup.md)

### A simpler read-only path exists

Outlook can publish a calendar as a read-only ICS feed. Glasswork could consume
that feed without OAuth, an Azure app registration, Graph, or write
permissions. This has drawbacks: organizations may disable publishing,
refreshes can be delayed, and setup is manual. It is nevertheless a useful
prototype path because read-only meetings are the intended boundary.

### WinUI has no hourly planner control

WinUI's `CalendarView` is a date-selection control. It does not provide an
Outlook-style day/week agenda, timed blocks, drag-and-drop scheduling, or
resizing. The Windows Community Toolkit does not provide such a scheduler
either. A native Glasswork planner would therefore require a custom
Canvas/ItemsControl-based hour grid or a commercial scheduler component.

This custom interaction surface is the largest engineering risk.

Sources:

- [WinUI CalendarView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/calendar-view)
- [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows)

## Operon findings

Operon is a useful comparator because it is also local-first, vault-native, and
Markdown-backed.

Its Calendar includes:

- **Time Grid:** an hour-by-hour day/week grid with draggable and resizable
  task blocks.
- **Time Tracker Grid:** Planned, External, and Tracked lanes for comparing
  intent with actual time.
- **Multi-Week:** a longer-range block view without hourly detail.
- **Task Pool:** Overdue, Unscheduled, All, and Finished task lists beside the
  calendar, from which tasks can be dragged onto the grid.
- **External calendars:** read-only ICS feeds with configurable refresh;
  Operon does not integrate with Graph, OAuth, or Teams.
- **Markdown persistence:** dragging or resizing a task updates scheduling
  metadata in the task's Markdown.

Operon does **not** document an automatic scheduler, daily capacity limit, or
warning when planned work exceeds available time. It primarily solves
visualization and manual placement, not overcommitment detection.

Sources:

- [Operon repository](https://github.com/hasanyilmaz/operon)
- [Calendar overview](https://raw.githubusercontent.com/hasanyilmaz/operon/main/docs/operon-docs/DOCS-028%20Calendar%20overview.md)
- [Calendar presets and time grid](https://raw.githubusercontent.com/hasanyilmaz/operon/main/docs/operon-docs/DOCS-029%20Calendar%20presets%20and%20time%20grid.md)
- [Calendar Task Pool](https://raw.githubusercontent.com/hasanyilmaz/operon/main/docs/operon-docs/DOCS-095%20Calendar%20Task%20Pool.md)
- [External calendars](https://raw.githubusercontent.com/hasanyilmaz/operon/main/docs/operon-docs/DOCS-048%20External%20calendars.md)

## Comparable product patterns

| Product | Relevant pattern | Capacity support | Scope warning |
| --- | --- | --- | --- |
| Operon | Task Pool beside draggable hourly grid; read-only ICS | No documented over-capacity cue | Manual visualization only |
| Sunsama | Daily list, backlog, time estimates, drag-to-timebox | Predicted workload compared with configured capacity | Broader two-way calendar integration |
| TaskNotes | Vault tasks with calendar views and Microsoft OAuth | No documented capacity cue | Bidirectional sync is optional but substantial |
| Motion | Durations, working hours, calendar overlay | Algorithm schedules around constraints | Full auto-scheduling and continuous optimization |
| Reclaim | Tasks and habits placed around calendar conflicts | Algorithmic rescheduling | Full calendar optimization product |

The convergence across Operon, Sunsama, TaskNotes, and Morgen is a task pool
beside a time-based view. Motion and Reclaim demonstrate the likely
scope-creep endpoint: once a planner exists, pressure grows to add
auto-scheduling and continuous conflict resolution.

Sources:

- [Sunsama timeboxing](https://help.sunsama.com/docs/usage-guides/timeboxing/)
- [Sunsama backlog](https://help.sunsama.com/docs/usage-guides/backlog/)
- [Sunsama daily planning](https://help.sunsama.com/docs/usage-guides/daily-planning/)
- [TaskNotes calendar integration](https://tasknotes.dev/features/calendar-integration/)
- [TaskNotes calendar views](https://tasknotes.dev/views/calendar-views/)
- [Motion auto-scheduling](https://www.usemotion.com/help/time-management/auto-scheduling/auto-scheduling-how-to-guide)

Product documentation establishes that these interactions exist, not that they
improve productivity for every user.

## Product boundary for Glasswork

### Smallest hypothesis

The hypothesis is:

> Comparing estimated work with available hours will help the user choose a
> realistic My Day and make it feel less daunting.

An hourly grid, calendar integration, and automatic scheduling are possible
solutions, but they are not the hypothesis itself.

### Recommended staged validation

1. **Capacity cue in My Day:** compare estimated Task/Subtask duration with a
   configured daily work budget. This is the cheapest way to determine whether
   visible overcommitment is enough to improve planning.
2. **Local-only Planner:** if the cue is insufficient, prototype a separate
   hourly Planner page with a My Day task pool and Glasswork-owned time blocks.
3. **Read-only ICS overlay:** add meetings without OAuth to test whether real
   calendar context materially improves the planner.
4. **Microsoft Graph/MSAL:** add native Microsoft 365 authentication and
   `calendarView` polling only if ICS is unavailable or too stale in the
   user's actual tenant.

### Recommended information architecture

If an hourly surface proves useful, it should be a separate **Planner** page,
not a replacement or alternate mode for My Day.

My Day answers **what is in scope today** through direct pins, virtual
promotion, due dates, and dismissal rules. Planner would answer **when the
selected work fits**. Keeping them separate:

- preserves My Day's simple daily-list model;
- lets Planner reuse My Day as its default unscheduled pool;
- avoids coupling time-block interactions to My Day's promotion rules;
- makes the experimental page easy to remove if it does not help.

### Data ownership

- **Vault:** existing Task/Subtask truth and, only after an explicit future
  decision, durable scheduling metadata that must travel across devices.
- **UI State:** first-version local task-block placement, because it describes
  the user's view and plan rather than the Task itself.
- **Calendar cache:** external read-only meeting data, stored locally and never
  merged into the Vault.

Whether planned times are durable Task truth or per-device UI state is an
ADR-level decision. UI State is the safer prototype default.

### Explicit non-goals

- No embedded Teams calendar UI; Microsoft does not provide one.
- No creation, editing, or deletion of Microsoft 365 meetings.
- No two-way task/event synchronization.
- No automatic scheduler or conflict optimizer.
- No multi-account, delegate-calendar, or team scheduling.
- No recurrence engine in Glasswork; external recurrence is expanded by Graph
  or the ICS parser.

## Recommendation

Do not begin with Microsoft integration or an hourly grid. First test a My Day
capacity cue using estimated durations and a daily-hours budget.

If that is insufficient, build a separate local-only Planner prototype with a
My Day task pool and custom hourly grid. Add read-only meetings only after the
manual planning interaction proves useful, using ICS first and Microsoft Graph
only if the user's tenant makes ICS impractical.

This sequence targets the real problem while preserving a firm boundary
against becoming a calendar client or auto-scheduling product.
