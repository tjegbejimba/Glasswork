# ADR 0021: Research Topics remain Wiki Pages with a namespaced opt-in

**Status**: Accepted

## Context

Glasswork needs a first-class Research surface for durable subjects such as
async callbacks, manifests, Redis, and learning topics. The Vault already
contains the authoritative synthesis as schema-governed Wiki Pages, often
connected to Projects, Concepts, Systems, Sources, people, decisions, Tasks, and
Wayfinder work. A separate Research document or workflow object would duplicate
that knowledge, while a machine-local saved selection would fail to sync the
page's durable role across machines.

The feature also crosses an existing architectural line: ADR 0005 assumes
Glasswork writes only under `wiki/todo/`, so the vault-wide Backlinks watcher
cannot observe a Glasswork self-write. First-class in-app opt-in and durable
Research Change Logs require narrowly scoped writes outside that directory.

## Decision

A **Research Topic** is an existing schema-governed Wiki Page, not a copied
document, Task, notebook, or workflow record. Any eligible Wiki Page opts in
through the presence of a namespaced block:

```yaml
glasswork:
  research:
    include: [optional-wiki-page-id]
    exclude: [optional-wiki-page-id]
    related_work: [optional-task-id]
    related_wayfinder: [optional-owner/repository#issue]
```

The Topic's live **Research context** contains the Topic itself plus
schema-governed Wiki Pages connected by one direct outgoing Wiki link,
provenance reference, or Backlink, adjusted by the durable include/exclude
overrides. Traversal stops after one hop. Research Change Logs, Tasks, and
arbitrary Vault Markdown never enter this context implicitly.

Glasswork gains a narrow Research writer outside `wiki/todo/`. It may mutate
only `glasswork.research` metadata and files under
`<vault>/wiki/research-logs/`; it never edits Topic synthesis prose. Every such
write participates in `SelfWriteCoordinator`. The implementation must revise
ADR 0005's watcher assumption because the vault-wide pipeline can now observe
Glasswork writes.

Each write-producing Research Session appends one dated summary and links to
changed Wiki Pages to the Topic's Research Change Log. The log is not chat
history, is excluded from Research context, and is hidden from the main Topic
synthesis. Confirmed **Remove from Research** preserves the Wiki Page, removes
the opt-in block, and permanently deletes the log; opting in again starts a new
history.

Research has no status, due date, completion, archive, or question lifecycle.
Existing Wiki `confidence`, `updated`, `expires`, and `sources` fields express
trust and freshness. Open Questions remain prose. Tasks and Wayfinder
maps/tickets are explicitly linked **Related Work** whose lifecycle never
changes the Topic.

Task Related Work uses reciprocal references without copying workflow state.
The Topic keeps only canonical Task IDs in `glasswork.research.related_work`;
each Task keeps the Topic Wiki link in its existing `## Related` section.
Glasswork resolves Task title and status live from the Task Index. Missing,
malformed, or one-sided references remain visible as repairable relationship
state rather than being treated as healthy or silently removed.

Wayfinder Related Work stores only canonical GitHub issue identities in
`glasswork.research.related_wayfinder`. Glasswork resolves the issue title and
open/closed state from GitHub when available, keeps inaccessible or unknown
state explicit, and never copies that lifecycle into Research metadata. Linking
an issue adds a guarded reciprocal comment containing the Topic deep link when
GitHub permits it. Missing issues and one-sided references remain visible and
repairable; external navigation is restricted to the trusted canonical GitHub
issue URI.

## Considered options

- **Machine-local UI State** was rejected because Research membership describes
  the durable Wiki Page and must sync with the Vault.
- **A separate Research manifest or copied synthesis** was rejected because it
  creates a second source of truth and weakens direct Obsidian/agent editing.
- **Automatic inference from type, tags, folders, or content** was rejected
  because agent grounding must remain deliberate and inspectable.
- **A general Wiki browser** was rejected because the value is a focused,
  opted-in return surface with bounded context, not exposing every Vault file.
- **Research workflow state** was rejected because knowledge may remain useful
  indefinitely and may produce zero, one, or many independent work items.

## Consequences

- The Wiki remains the durable knowledge model; Glasswork provides a live lens
  and scoped agent/work handoffs.
- Topic membership and context overrides are inspectable, syncable, reversible,
  and many-to-many without moving pages.
- The Research writer must preserve all unrelated frontmatter and prose, make
  multi-file mutations safely, and register self-writes.
- The existing Backlinks watcher topology must be updated so metadata and log
  writes do not create spurious external-change behavior.
- The Wiki schema must document `glasswork.research` and
  `wiki/research-logs/` before implementation writes real user data.
