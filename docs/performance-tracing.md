# Performance tracing

Glasswork has an opt-in local performance trace for comparing startup and refresh behavior. It writes JSON Lines to disk and `Debug`; it does not use OpenTelemetry, send data over the network, or write to the Vault.

## Enable tracing

Set `GLASSWORK_PERF_TRACE=1` before launching the app:

```powershell
$env:GLASSWORK_PERF_TRACE = '1'
dotnet run --project src\Glasswork.App\Glasswork.csproj -p:Platform=x64
```

By default, each primary app process creates a unique file under `%TEMP%`:

```text
glasswork-perf-<UTC timestamp>-<process id>.jsonl
```

Set an exact output path when a script needs a predictable filename:

```powershell
$env:GLASSWORK_PERF_TRACE = '1'
$env:GLASSWORK_PERF_TRACE_PATH = "$env:TEMP\glasswork-perf.jsonl"
dotnet run --project src\Glasswork.App\Glasswork.csproj -p:Platform=x64
```

An explicit path is replaced when a new primary app process starts. Only one process may write an explicit path at a time; another process that finds it open disables tracing rather than interleaving JSONL records. Redirected secondary instances do not create trace files.

Disable tracing by removing the variables:

```powershell
Remove-Item Env:GLASSWORK_PERF_TRACE -ErrorAction Ignore
Remove-Item Env:GLASSWORK_PERF_TRACE_PATH -ErrorAction Ignore
```

Trace files persist until manually deleted.

## Inspect a trace

```powershell
Get-Content $env:GLASSWORK_PERF_TRACE_PATH |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Format-Table event, duration_ms, elapsed_ms, task_count, outcome
```

Each line contains:

- `ts`: UTC completion time
- `session_id`: unique ID for one primary app process
- `event`: stable measurement name
- `kind`: `span` or `milestone`
- `duration_ms`: span duration, omitted for milestones
- `elapsed_ms`: completion time relative to the earliest managed `App` constructor hook
- `thread_id`: managed thread that completed the measurement
- `outcome`: `ok` or `error`
- optional low-cardinality counts or mode tags

The trace never records Vault paths, task IDs, titles, task prose, or search text.

`elapsed_ms` excludes native process bootstrap, CLR startup, and work before the managed `App` constructor. First-frame events use the first WinUI composition callback after the relevant UI work is ready; they are not GPU presentation telemetry.

## Events

| Event | Measurement |
|---|---|
| `vault.services_initialize` | Required Task-service initialization through coherent publication; supplemental scans are separate |
| `vault.backlink_index_hydrate` | Supplemental recursive Backlink index hydration |
| `vault.research_catalog_hydrate` | Supplemental Research Catalog hydration |
| `vault.task_startup_hydration` | Single-pass managed recovery, V1 migration, final-byte parsing, Resource Revision calculation, and atomic Index seed; includes `task_count`, `migrated_task_count`, and `examined_file_count` |
| `vault.my_day_pin_migration` | Date-scoped My Day pin migration |
| `app.window_first_frame` | Loading-shell milestone at the first composition callback after the main window is activated; Vault-dependent initialization starts only after this callback |
| `app.tasks_ready` | Milestone after one coherent Vault service generation is published and the initial Task page is constructed; supplemental Backlink/Research readiness is separate |
| `app.backlinks_ready` | Milestone after the supplemental Backlink snapshot and lossless watcher handoff are ready |
| `app.research_ready` | Milestone after the supplemental Research snapshot and lossless watcher handoff are ready |
| `my_day.refresh_data` | My Day query, grouping, and collection reconciliation; excludes later WinUI layout and painting |
| `my_day.initial_render` | First My Day navigation through its first post-refresh composition callback, once per page instance |
| `backlog.refresh_data` | Backlog filtering, sorting, grouping, and collection mutation; excludes later ListView/board layout and scroll restoration |

Vault initialization events also appear after an in-app Vault switch. My Day and Backlog refresh events appear for every refresh source, including watcher updates and property-driven refreshes.

## Compare runs

Use the same seeded Vault and launch path for both runs. Compare event medians over several cold launches rather than treating one duration as definitive. The trace intentionally records measurements without enforcing machine-dependent performance thresholds.

Completed records are written and flushed synchronously for reliability. Use the default temporary path or another local path; a slow or network-backed override can add diagnostic overhead to the UI thread and to enclosing measurements.
