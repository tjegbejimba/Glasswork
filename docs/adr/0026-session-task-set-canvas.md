# ADR 0026: Session Task Set canvas shares the Task Detail projection

**Status**: Accepted
**Amends**: ADR 0001 and ADR 0012

## Context

The existing project-scoped `glasswork-task` canvas reads and parses one Task
directly in JavaScript. Its reduced status model, Markdown support, artifact
handling, and recursively embedded child details already differ from native
Task Detail. Maintaining another parser and presentation model cannot provide
durable parity as Glasswork evolves.

Copilot canvas extensions receive a stable session ID and can restore an open
canvas after a cold session resume, but the extension process and page URL are
transient. A `createCanvas` extension also has no generic SDK path for invoking
same-session MCP tools. The design therefore needs a local bridge that shares
Glasswork's .NET model without changing the requested canvas experience.

## Decision

Glasswork Core owns a presentation-neutral **Task Detail Projection** consumed
by native Task Detail and the Glasswork Tasks canvas. Any semantic Task Detail
change updates the projection and both renderers together. Each renderer uses
its platform's theme primitives, but section hierarchy, labels, content, and
state semantics remain aligned.

The user-scoped extension keeps a thin Node SDK adapter and launches one
self-contained .NET canvas host per Copilot session. The host references Core,
owns projection, UI State access, and live Vault observation, and serves the
canvas on an ephemeral loopback port. It is bound to the extension process
lifetime, holds no mutable state shared with other sessions, and keys persisted
state by the SDK-provided session ID.

The canvas is a read-only, responsive master-detail surface:

- The **Session Task Set** is an explicit, recency-ordered set of at most 20
  Tasks. Load, unload, clear, select, and Task-reference navigation are explicit
  membership actions; Task status and agent activity never infer membership.
- UI State persists Task IDs, order, and last-known titles for the same Copilot
  session. Missing Tasks remain unavailable members until explicitly removed
  and are exempt from generic stale Task-ID garbage collection. No Task prose
  is cached outside the Vault.
- The rail uses compact operational metadata. The selected detail loads lazily,
  shows direct Children only, and omits edit, lifecycle, and Hard-deletion
  controls. Safe refresh, copy, Glasswork, Obsidian, and policy-governed link
  actions remain.
- Vault changes refresh affected content automatically without changing
  selection or reading position. A failed refresh retains the last good view
  and surfaces an exact stale/error state.

The extension and canvas host ship in the app release stream at the app version.
App installation places a user-scoped extension bundle that is available in
every Copilot session and works without the native app process running. Bundles
activate side-by-side: new sessions use the newly verified version, running
sessions keep their loaded version and receive a non-blocking update signal,
and old bundles are removed only after no process uses them. Canvas installation
failure remains visible and retryable but does not make the native app unusable.

The existing `glasswork-task` canvas ID and singular `task_id` input remain
backward compatible while the contract adds multi-Task input and membership
actions.

## Considered Options

- **Independent JavaScript parser and renderer** was rejected because it
  duplicates domain policy and cannot enforce ongoing Task Detail parity.
- **MCP App** was rejected because it changes the requested canvas extension
  surface even though it could invoke app-visible MCP tools.
- **Fixed-port or global canvas host** was rejected because it couples
  concurrent sessions and introduces shared process state.
- **Independent canvas release stream** was rejected because app/canvas version
  skew would undermine the shared parity contract.

## Consequences

- Native and canvas Task Detail have one semantic contract and can be verified
  with shared projection fixtures.
- Concurrent sessions pay for one bounded helper and watcher set each, while
  remaining isolated and read-only against the same Vault.
- App publication now owns a user-extension bundle, side-by-side activation,
  health reporting, and eventual old-version cleanup.
- Verification requires Core projection contracts, black-box canvas-host
  scenarios, and side-by-side native/canvas visual scenarios; pixel equality
  across WinUI and HTML is not a requirement.
