# Research: Microsoft 365 Calendar Architecture for Glasswork's Planner

**Status:** Research only. No production implementation.

**Question:** What Microsoft identity, Graph, caching, normalization, and
distribution architecture can safely provide read-only calendar context to the
today-only, capacity-first Planner?

## Relationship to prior research

The product boundary is already established by `docs/research/day-planner.md`
and `docs/research/adhd-planning.md` at commit
`3e3f9865721b5317be30c3803d699ae924947f0b`: Planner is separate from My Day,
today-only, capacity-first, and read-only. It is not a calendar client, a
two-way synchronization system, or an automatic scheduler. This report does
not repeat that research. It resolves the Microsoft 365 architecture needed by
the downstream ownership and capacity-algorithm decisions.

## Executive recommendation

Use a user-supplied Microsoft Entra **public-client** registration with no
secret. Authenticate with MSAL.NET and the Windows Web Account Manager (WAM)
broker. Request delegated `Calendars.Read` for the signed-in user's calendars,
not an application permission and not a `.Shared` permission. Discover all of
the user's calendars with `GET /me/calendars`, let the user select which
calendars affect Planner, and query each selected calendar's `calendarView`
for the local day.

Normalize Graph events behind a narrow Calendar Context boundary before they
reach Planner. That boundary emits local-time busy blocks, not Graph objects.
It excludes cancelled, declined, and `free` events; redacts sensitive events;
and preserves individual overlapping events for presentation while the
capacity algorithm subtracts the **union** of busy intervals so double-booked
time is counted once.

Persist MSAL's cache using Microsoft's cache extension. Keep a separate,
minimal, current-user-encrypted event cache under `%LocalAppData%\Glasswork\`;
never write calendar data to the Vault or `ui-state.json`. Authentication,
consent, Conditional Access, network, and throttling failures should produce a
typed calendar-unavailable state while Planner continues without meeting
context.

This is the recommended architecture for the downstream ownership and
algorithm tickets. The remaining unknowns are tenant provisioning facts, not
architecture decisions: whether the target tenant permits app registration and
user consent, and which Conditional Access policies apply.

## 1. Identity and WAM

### Verified facts

- A native desktop app is a **public client**. Microsoft directs desktop apps
  to add the "Mobile and desktop applications" platform and enable public
  client flows; no client secret is used
  ([desktop app configuration](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration)).
- WAM is the Windows authentication broker. It provides Windows-account
  integration, silent SSO, Windows Hello, FIDO, token protection, and
  Conditional Access support. It is available on Windows 10 version 1703 and
  later and Windows Server 2019 and later
  ([MSAL.NET with WAM](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)).
- MSAL.NET 4.52.0 or later supports WAM through
  `Microsoft.Identity.Client` and `Microsoft.Identity.Client.Broker`. WAM only
  supports Microsoft Entra ID; unsupported operating systems and unsupported
  authorities fall back to a browser
  ([MSAL.NET with WAM](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)).
- The registration must contain
  `ms-appx-web://Microsoft.AAD.BrokerPlugin/{client_id}` as a Mobile and desktop
  redirect URI. The WAM flow does not require that URI to be supplied in MSAL
  code
  ([acquire a token with WAM](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)).
- Microsoft's WAM pattern is: use a previously cached account if present,
  otherwise try `PublicClientApplication.OperatingSystemAccount`; call
  `AcquireTokenSilent`; on `MsalUiRequiredException`, call
  `AcquireTokenInteractive`
  ([acquire a token with WAM](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)).
- Interactive WAM must be parented to the app window with
  `WithParentActivityOrWindow`. WinUI 3 can obtain the HWND with
  `WinRT.Interop.WindowNative.GetWindowHandle`
  ([WAM parent handles](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam),
  [WinUI HWND](https://learn.microsoft.com/en-us/windows/apps/develop/ui-input/retrieve-hwnd)).
- A tenant-specific authority can show a generic Microsoft authentication
  prompt rather than the broad Windows account picker. An authority that also
  allows personal Microsoft accounts can show the native account picker
  ([MSAL.NET with WAM](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)).
- Glasswork's unpackaged, self-contained, non-single-file publish shape does
  not conflict with a documented WAM requirement. Microsoft explicitly
  addresses WinUI 3 and does not state an MSIX requirement. A future
  single-file publish would need
  `IncludeNativeLibrariesForSelfExtract=true` for WAM native binaries
  ([WAM troubleshooting](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)).

### Recommended design

Build one `IPublicClientApplication` for the process, enable WAM, register its
token cache before the first acquisition, and use the documented
cached-account -> operating-system-account -> interactive chain. Do not prompt
on Planner's first paint if silent acquisition can succeed. Require a parent
HWND for every interactive call.

Map expected outcomes such as picker dismissal, consent denial, tenant policy,
and no network to typed states. Log unexpected MSAL or cache failures using the
app's normal diagnostics and surface Calendar Context as unavailable; do not
swallow programming or data-corruption errors as success.

## 2. Permissions and least privilege

### Verified facts

| Delegated permission | Microsoft description | Admin consent required by default | Shared/delegate calendars |
| --- | --- | --- | --- |
| `Calendars.ReadBasic` | Read events except properties "such as body, attachments, and extensions" | No | No |
| `Calendars.Read` | Read events in user calendars | No | No |
| `Calendars.Read.Shared` | Read all calendars the user can access, including shared/delegate calendars | No | Yes |
| `Calendars.ReadWrite` | Create, read, update, and delete events in user calendars | No | No |

Source: [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference).

Microsoft marks `Calendars.ReadBasic` as the least-privileged permission for
both [listing calendars](https://learn.microsoft.com/en-us/graph/api/user-list-calendars?view=graph-rest-1.0)
and [listing a calendar view](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0).
However, Microsoft's permission description gives examples rather than a
complete property-level contract for what `ReadBasic` removes. The primary
sources reviewed do not expressly guarantee that every field needed by the
capacity policy, especially `showAs`, `isAllDay`, and `sensitivity`, is
returned under `ReadBasic`.

### Recommended design

Request delegated `Calendars.Read`. It is read-only and excludes
shared/delegate calendars, but supplies the complete event representation that
the availability policy needs. Do not request `Calendars.Read.Shared`,
`Calendars.ReadWrite`, or any application permission.

This is least privilege relative to the required, verified data contract, not
the smallest permission listed in the endpoint table. A target-tenant Graph
Explorer check can later prove that `Calendars.ReadBasic` returns every
required field and permit a scope reduction. Until then, silently depending on
an undocumented property subset is the riskier choice.

## 3. Exact Graph retrieval strategy

### Verified facts

- `GET /me/calendars` returns all the user's calendars. Graph also exposes
  narrower routes for the default or a specific calendar group
  ([list calendars](https://learn.microsoft.com/en-us/graph/api/user-list-calendars?view=graph-rest-1.0)).
- `GET /me/calendars/{id}/calendarView?startDateTime=...&endDateTime=...`
  returns single instances, recurring occurrences, and exceptions in the
  requested range. Recurrence expansion is server-side
  ([list calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)).
- The range values are ISO 8601. Their embedded offsets determine request
  interpretation; `Prefer: outlook.timezone` does not change the request
  bounds. A bound without an offset is treated as UTC
  ([list calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)).
- `calendarView` accepts a `$top` from 1 through 1000. A multi-page response
  contains `@odata.nextLink`
  ([list calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)).
- Graph paging requires following `@odata.nextLink` until absent. The link is
  an opaque continuation URL; clients should not reconstruct it
  ([Graph paging](https://learn.microsoft.com/en-us/graph/paging)).
- `event` documents `iCalUId` as unique across calendars and different for
  each occurrence in a recurring series
  ([event resource](https://learn.microsoft.com/en-us/graph/api/resources/event?view=graph-rest-1.0)).
- `calendarView` delta is intended to maintain a synchronized local store over
  a fixed range and requires `Calendars.Read`
  ([event delta](https://learn.microsoft.com/en-us/graph/api/event-delta?view=graph-rest-1.0)).

### Recommended request sequence

1. On sign-in and when calendar settings are opened, call:

   ```http
   GET https://graph.microsoft.com/v1.0/me/calendars
       ?$select=id,name,color,isDefaultCalendar,canEdit,canViewPrivateItems,owner
   ```

   Follow calendar-list pagination as well. Cache this discovery metadata in
   memory. Present an include/exclude selection because Graph cannot know
   whether Birthdays, Holidays, or another user calendar should reduce work
   capacity.

2. For each selected calendar, compute local midnight for the requested
   `DateOnly` and the next local midnight as separate `DateTimeOffset` values.
   Computing each boundary separately preserves the correct offset across a
   daylight-saving transition. Request:

   ```http
   GET https://graph.microsoft.com/v1.0/me/calendars/{id}/calendarView
       ?startDateTime={local-midnight-with-offset}
       &endDateTime={next-local-midnight-with-offset}
       &$select=id,iCalUId,subject,start,end,showAs,isAllDay,isCancelled,
                sensitivity,type,seriesMasterId,responseStatus
       &$orderby=start/dateTime
       &$top=250
   Prefer: outlook.timezone="{TimeZoneInfo.Local.Id}"
   ```

   URL-encode values and query syntax normally. If the SDK rejects
   `$orderby=start/dateTime` for this route, omit it and sort locally; local
   sorting is authoritative regardless. Follow every `@odata.nextLink`
   unchanged.

3. Normalize each page immediately, merge all selected calendars, deduplicate,
   and sort client-side. Do not expose or persist raw SDK event models.

4. Refresh when Planner becomes visible, after an explicit refresh, and at a
   coarse interval such as 5-10 minutes only while visible. Do not use delta
   or webhooks in v1. The bounded, foreground, today-only read does not justify
   a durable synchronization protocol or public callback endpoint.

## 4. Time zones

### Verified facts

`Prefer: outlook.timezone` controls the response time zone for event start and
end values; without it Graph returns UTC. It does not affect request-range
interpretation
([list calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)).
Graph's `dateTimeTimeZone` primarily uses Windows time-zone names and documents
some additional IANA names
([dateTimeTimeZone](https://learn.microsoft.com/en-us/graph/api/resources/datetimetimezone?view=graph-rest-1.0)).

Graph can return the mailbox's setting or supported Windows/IANA zones, but
those endpoints require `User.Read`
([mailbox settings](https://learn.microsoft.com/en-us/graph/api/user-get-mailboxsettings?view=graph-rest-1.0),
[supported time zones](https://learn.microsoft.com/en-us/graph/api/outlookuser-supportedtimezones?view=graph-rest-1.0)).

### Recommended design

Glasswork is Windows-only, so use `TimeZoneInfo.Local.Id`, which is a Windows
time-zone ID, and avoid adding `User.Read` only to discover a value the OS
already owns. Parse Graph values to `DateTimeOffset`, clip intervals to the
local-day or configured-workday bounds, and keep offsets until presentation.
Do not maintain a private Windows/IANA mapping table.

## 5. Event normalization and capacity semantics

### Verified facts

Graph defines:

- `showAs`: `free`, `tentative`, `busy`, `oof`, `workingElsewhere`, `unknown`;
- `isCancelled`;
- `isAllDay`, whose bounds are midnight in one time zone;
- `sensitivity`: `normal`, `personal`, `private`, `confidential`;
- `responseStatus`;
- `type`: `singleInstance`, `occurrence`, `exception`, `seriesMaster`;
- `seriesMasterId`; and
- `iCalUId`, which changes per recurring occurrence.

Source: [event resource](https://learn.microsoft.com/en-us/graph/api/resources/event?view=graph-rest-1.0).
`calendarView` returns expanded occurrences and exceptions
([list calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)).
Microsoft does not promise in the reviewed reference that cancelled
occurrences are always omitted from `calendarView`; consumers should honor
`isCancelled` defensively.

### Recommended normalization policy

| Graph signal | Timeline representation | Capacity effect |
| --- | --- | --- |
| `isCancelled == true` | Omit | Ignore |
| `responseStatus.response == "declined"` | Omit | Ignore |
| `showAs == "free"` | Optional faint context | Ignore |
| `showAs == "tentative"` | Distinct tentative block | Busy by default |
| `showAs == "busy"` | Busy block | Busy |
| `showAs == "oof"` | Out-of-office block | Busy |
| `showAs == "workingElsewhere"` | Distinct context block | Free by default; location is not unavailability |
| `showAs == "unknown"` or a future value | Unknown block | Busy, fail-safe |
| `private`, `personal`, or `confidential` | Generic "Private event"; no subject/location | Apply the event's availability |
| `isAllDay == true` | All-day banner | Apply `showAs`; a busy/OOF all-day event intersects the whole configured work window |

For duplicates, first remove exact repeats by
`(iCalUId, normalized start, normalized end)`. Preserve the calendar source and
Graph `id` for diagnostics within the refresh cycle, but do not use the
container-relative `id` as a cross-calendar identity.

For distinct overlaps, preserve each event for rendering. The capacity
algorithm must clip busy blocks to the work window and subtract the duration
of their **interval union**, not the sum of event durations. Thus two
simultaneous one-hour meetings consume one hour, not two. Touching intervals
may be coalesced. Zero- or negative-duration events are invalid input and
should be logged and discarded.

This policy intentionally uses only availability, response, sensitivity, and
time. It does not inspect attendees, body, attachments, online-meeting details,
or location, and therefore does not turn Planner into a calendar client.

## 6. Throttling, retries, and partial failure

### Verified facts

Graph returns HTTP 429 with `Retry-After` when throttled. Microsoft says to
wait for that duration, retry, and continue honoring subsequent `Retry-After`
values. Without the header, use exponential backoff. Graph SDKs provide retry
middleware that honors `Retry-After`
([Graph throttling](https://learn.microsoft.com/en-us/graph/throttling)).
The Graph SDK retry design covers 429, 503, and 504 responses and bounds retry
attempts
([SDK retry design](https://github.com/microsoftgraph/msgraph-sdk-design/blob/master/middleware/RetryHandler.md)).

### Recommended design

Use the Graph .NET SDK retry middleware, configure at most three retries, and
do not layer an independent retry loop around it. Keep the visibility-based
poll cadence low. If one calendar fails after retries, return a typed partial
result that identifies stale or unavailable calendar context rather than
presenting a falsely complete free-time total. Retain the last valid same-day
encrypted cache with a visible "last updated" timestamp; never reinterpret a
failed fetch as an empty calendar.

## 7. Token-cache security

### Verified facts

WAM does not remove the need to persist MSAL's cache: MSAL continues to store
ID tokens and account metadata there
([WAM token-cache persistence](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)).
For desktop applications Microsoft recommends
`Microsoft.Identity.Client.Extensions.Msal`, registered against
`pca.UserTokenCache`. On Windows its secure storage uses an encrypted file;
Microsoft's Windows serialization example uses DPAPI
`ProtectedData` with `DataProtectionScope.CurrentUser`
([MSAL token-cache serialization](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization?tabs=desktop)).
An unprotected-file fallback exists, but it is explicitly opt-in.

For desktop public clients, Microsoft documents clearing the cache by
enumerating accounts and calling `RemoveAsync` for each. This does not clear a
browser session cookie
([clear the MSAL cache](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/clear-token-cache)).

### Recommended design

Use `Microsoft.Identity.Client.Extensions.Msal`; do not write a custom token
serializer. Store its dedicated cache under `%LocalAppData%\Glasswork\`, not
in `ui-state.json`. Do not enable unprotected fallback. If protected
persistence is unavailable, report Calendar Context unavailable rather than
writing tokens or metadata in plain text.

An explicit "Disconnect Microsoft 365" action should call `RemoveAsync` for
the app's cached accounts and delete the event cache. Do not claim that this
removes the account from Windows or signs the user out of Microsoft globally;
it disconnects Glasswork and clears Glasswork's MSAL cache.

## 8. Offline event-cache privacy and lifecycle

Microsoft does not prescribe a calendar-event cache format for this scenario;
the following is a Glasswork design decision.

- Store a separate file such as
  `%LocalAppData%\Glasswork\calendar-cache.dat`. It belongs neither in the
  Vault nor in `ui-state.json`: it is external service data, not Task truth or
  a view preference.
- Encrypt with Windows DPAPI using `DataProtectionScope.CurrentUser`. Do not
  fall back silently to plaintext.
- Persist only calendar source ID, normalized start/end, normalized
  availability, all-day state, redaction state, optional redaction-safe
  subject, fetch timestamp, and local calendar date. Never persist bodies,
  attendees, attachments, locations, join URLs, or raw Graph payloads.
- Replace atomically after a complete successful refresh. A partial refresh
  must not overwrite a known-complete snapshot.
- Expire at the next local midnight. Also delete on disconnect, account
  change, client/tenant configuration change, or explicit cache reset.
- Use stale same-day data only as visibly stale fallback. Never carry it into
  the next day and never treat missing or failed data as proof of free time.
- Keep all cache writes outside the Vault, so `SelfWriteCoordinator` is not
  involved.

## 9. Consent and Conditional Access

### Verified facts

The delegated calendar permissions above do not require admin consent by
default
([Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)).
Tenant administrators can nevertheless disable or restrict user consent, in
which case an administrator must grant consent
([admin consent protocol](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent)).

Conditional Access can require MFA, a compliant or managed device, an allowed
network, or other conditions, and can block authentication. WAM supports
Conditional Access interaction but cannot bypass policy
([Conditional Access](https://learn.microsoft.com/en-us/entra/identity/conditional-access/overview),
[MSAL.NET with WAM](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)).

For multitenant applications, publisher verification affects consent trust and
some risk-based consent policies. A verified publisher badge requires the
publisher-verification process
([publisher verification](https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview)).

### Recommended design

Classify consent denial, admin approval required, and Conditional Access denial
as distinct typed states so Settings can explain the next action. Do not
repeatedly prompt or retry a policy denial. Planner remains usable without
calendar context.

The app cannot guarantee integration before testing the actual tenant. Record
tenant provisioning as a prerequisite, not as a runtime workaround.

## 10. Registration and source-visible distribution

### Verified facts

A desktop public client has no client secret, so a client ID visible in source
is not a leaked credential. Supported account types are a registration choice:
single tenant, multiple organizational tenants, organizational plus personal
Microsoft accounts, or personal accounts
([supported account types](https://learn.microsoft.com/en-us/entra/identity-platform/v2-supported-account-types)).

Glasswork is a source-visible, single-user app built locally rather than a
centrally distributed multitenant SaaS client. That changes the operational
trade-off but not Microsoft's protocol.

### Recommended design

For the intended single-user deployment, use a **user- or administrator-created
registration dedicated to this Glasswork installation**:

- choose single-tenant when the calendar is in one work/school tenant;
- if the target is a personal Microsoft account, use a developer-owned
  registration whose supported account type includes personal accounts;
- configure the WAM redirect URI and public-client flow;
- grant only delegated `Calendars.Read`; and
- place client ID and tenant/authority in local, uncommitted configuration.

Do not commit a shared publisher-owned client ID that silently turns every
source clone into one multitenant application. That model would require the
project owner to operate a stable registration, choose broader supported
account types, handle external-tenant consent, and likely pursue publisher
verification.

If the target organization prevents the user from creating registrations or
granting consent, an administrator must create/approve the registration.
Glasswork cannot automate around that policy.

## Downstream contract

Introduce a Calendar Context boundary parallel to UI State. It is the only
layer that knows MSAL, Graph, calendar IDs, paging, or cache serialization.
Exact names belong to the implementation design, but the semantic contract
should be:

```text
CalendarConnectionState:
  Disconnected | Ready | ConsentRequired | AdminApprovalRequired |
  PolicyBlocked | Offline | TemporarilyUnavailable

CalendarDaySnapshot:
  Day
  FetchedAt
  Freshness: Fresh | Stale
  Completeness: Complete | Partial
  Blocks: CalendarBlock[]

CalendarBlock:
  Start: DateTimeOffset
  End: DateTimeOffset
  Availability: Busy | Free | Ignore
  IsAllDay
  IsTentative
  IsSensitive
  DisplaySubject?
```

Calendar Context must never expose Graph SDK models, raw event payloads,
attendees, body, attachments, or write methods. Expected operational failures
return typed states; unexpected failures are logged and propagated according
to existing app policy.

The capacity algorithm remains pure Core logic. It accepts the configured work
window, the Planner's coarse work-size budget, and normalized blocks. It clips
blocks to the work window, discards `Free`/`Ignore`, unions the remaining
intervals, and subtracts the union duration exactly once. It has no network,
MSAL, Graph, cache, or WinUI dependency.

## Tenant-specific checks before implementation

These are precise provisioning checks, not additional architecture decisions:

1. Identify whether the target calendar account is organizational or personal.
2. Confirm who can create the app registration in the target directory.
3. Confirm whether user consent for delegated `Calendars.Read` is allowed.
4. Confirm whether Conditional Access permits the actual Windows device.
5. Run the public-client/WAM sign-in flow from the unpackaged debug build.
6. Inspect `Calendars.ReadBasic` in Graph Explorer only if scope reduction is
   worth pursuing.
7. Inspect `/me/calendars` to choose sensible default included calendars.

## Primary sources

- [Configure desktop apps that call web APIs](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration)
- [Acquire a token with WAM](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-acquire-token-wam)
- [Using MSAL.NET with WAM](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam)
- [Retrieve a WinUI window handle](https://learn.microsoft.com/en-us/windows/apps/develop/ui-input/retrieve-hwnd)
- [MSAL.NET token-cache serialization](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization?tabs=desktop)
- [Clear the MSAL.NET token cache](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/clear-token-cache)
- [Supported account types](https://learn.microsoft.com/en-us/entra/identity-platform/v2-supported-account-types)
- [Admin consent protocol](https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent)
- [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)
- [List calendars](https://learn.microsoft.com/en-us/graph/api/user-list-calendars?view=graph-rest-1.0)
- [List calendarView](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)
- [Event resource](https://learn.microsoft.com/en-us/graph/api/resources/event?view=graph-rest-1.0)
- [dateTimeTimeZone resource](https://learn.microsoft.com/en-us/graph/api/resources/datetimetimezone?view=graph-rest-1.0)
- [Get user mailbox settings](https://learn.microsoft.com/en-us/graph/api/user-get-mailboxsettings?view=graph-rest-1.0)
- [List supported time zones](https://learn.microsoft.com/en-us/graph/api/outlookuser-supportedtimezones?view=graph-rest-1.0)
- [Event delta](https://learn.microsoft.com/en-us/graph/api/event-delta?view=graph-rest-1.0)
- [Graph paging](https://learn.microsoft.com/en-us/graph/paging)
- [Graph throttling](https://learn.microsoft.com/en-us/graph/throttling)
- [Graph SDK retry design](https://github.com/microsoftgraph/msgraph-sdk-design/blob/master/middleware/RetryHandler.md)
- [Conditional Access overview](https://learn.microsoft.com/en-us/entra/identity/conditional-access/overview)
- [Publisher verification](https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview)
