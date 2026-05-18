# ADR 0009: Structured links use a typed frontmatter list

**Status**: Accepted
**Context slice**: Structured links PRD #125; resolves issue #126

## Context

Before this slice, a task had one structured external pointer: the legacy
`ado_link` frontmatter field, with `ado_title` as optional display text. Other
references that help a human or agent pick up a task — pull requests, incidents,
builds, eng.ms docs, and arbitrary URLs — lived in prose inside Description,
Notes, or ad-hoc fields. That made the references hard to scan, hard to preserve
when prose changed, and expensive for agents to rediscover.

Structured links need to coexist with the three-tier task prose model from ADR
0002 without becoming a fourth prose tier. They are task metadata in frontmatter:
machine-readable pointers that complement Description, Notes, and Artifacts.
They are also distinct from Wiki links and Backlinks. Wiki links are markdown
references inside rendered prose; Backlinks are incoming wiki references from
non-task pages. Structured links are explicit outbound task metadata.

The design also has to preserve existing `ado_link` behavior. Existing tasks
must continue to show their ADO chip and ADO title after the format change, and
the on-disk format should converge without a one-time vault rewrite.

## Decision

Adopt a `links:` frontmatter field containing an ordered list of typed entries:

```yaml
links:
  - type: ado
    value: 1234
    label: IAM routing fix
  - type: pr
    value: https://github.com/tjegbejimba/Glasswork/pull/123
  - type: incident
    value: ICM 965114
  - type: doc
    value: https://eng.ms/docs/products/example
  - type: build
    value: https://dev.azure.com/example/_build/results?buildId=123
  - type: other
    value: https://example.com/context
    label: Whitepaper
```

Each Link has:

| Field | Required? | Meaning |
|---|---:|---|
| `type` | Yes | Recognized v1 values are `ado`, `pr`, `incident`, `doc`, `build`, and `other`. Unknown values are tolerated and surfaced as `other`. |
| `value` | Yes | The target identifier or URL, stored uniformly by the model even when YAML authors write a number. |
| `label` | No | Human-friendly display text. When omitted, the app may derive a type-specific display value such as `ADO #1234`. |

Use a typed list instead of a flat keyed dictionary such as
`links: { ado: 1234, pr: [...] }`.

The list shape is canonical because it:

1. Preserves author-chosen ordering.
2. Allows multiple Links of the same type without special-case array semantics.
3. Keeps optional per-Link `label` data local to the Link it describes.
4. Leaves room for future per-Link metadata without changing the top-level
   schema again.

`ado_link` and `ado_title` become legacy compatibility fields. On parse, a task
with only legacy ADO fields hydrates an `ado` Link into the model. On serialize,
the canonical `links:` field is emitted and `ado_link` / `ado_title` are not
written back. If both `links:` and legacy ADO fields are present, `links:` wins.
This is a lazy on-save migration: old files remain valid indefinitely, and files
converge only when Glasswork next saves them. This follows the same philosophy as
the V1-to-V2 task body migration: transform on read, write the canonical shape,
and avoid a flag-day vault rewrite.

The v1 UI boundary is read-only in-app. TaskDetail may render a Links section,
but editing Links happens in Obsidian or by editing YAML directly. In-app add,
remove, and edit affordances are deferred.

Any UI that launches a structured Link must use the same untrusted-link safety
boundary as rendered markdown: route launch decisions through `ArtifactLinkPolicy`
or its direct successor. Link values are vault content and may be agent-produced.

## Alternatives considered

### A. Keep `ado_link` and add one field per new type

- ✅ Minimal parser churn for each individual type.
- ❌ Multiplies bespoke frontmatter keys (`pr_link`, `incident_link`,
  `doc_link`, etc.).
- ❌ Cannot represent multiple links of the same type cleanly.
- ❌ Makes optional labels inconsistent or requires a second field per type.
- **Rejected** — repeats the exact shape limitation that structured links are
  meant to solve.

### B. Flat keyed dictionary

```yaml
links:
  ado: 1234
  pr:
    - https://github.com/tjegbejimba/Glasswork/pull/123
```

- ✅ Compact for the common one-link-per-type case.
- ❌ Ordering is implicit and type-grouped instead of author-controlled.
- ❌ Multiples require type-specific scalar-vs-list rules.
- ❌ Optional labels become awkward sibling data or nested special cases.
- **Rejected** — compactness is not worth the long-term schema complexity.

### C. Markdown-only links in Description or Notes

- ✅ Zero schema change.
- ✅ Authors can already paste URLs in prose.
- ❌ Agents and app code must parse prose to discover task context.
- ❌ Links are mixed with narrative text and can be lost during prose edits.
- ❌ No typed badge, no predictable migration path for the existing ADO chip.
- **Rejected** — does not solve the structured-context problem.

### D. Bulk migrate every vault task immediately

- ✅ Converts the entire vault to the new shape in one operation.
- ❌ Surprising, high-blast-radius write across user-authored files.
- ❌ Requires additional UX, rollback, and conflict handling.
- ❌ Unnecessary because legacy fields can be read indefinitely.
- **Rejected** — lazy on-save migration is safer and consistent with prior
  vault migrations.

### E. Add in-app editing in v1

- ✅ More complete user workflow.
- ❌ Expands the slice from schema/rendering into collection editing UX,
  validation, conflict handling, and save semantics.
- ❌ Increases the chance of getting the durable on-disk shape wrong.
- **Rejected for v1** — the YAML surface is sufficient while the schema settles.

## Consequences

### Good

- A task gains one structured place for external context: ADO work items, PRs,
  incidents, docs, builds, and arbitrary labeled URLs.
- The schema supports ordering, duplicates, and labels from the start.
- Existing ADO behavior can remain source-compatible through a derived `AdoLink`
  projection over the first `Links[type=ado]` entry.
- Unknown future Link types do not break parsing; they degrade to `other`.
- Migration is incremental and avoids a bulk vault rewrite.

### Bad / accepted trade-offs

- There are now two on-disk ADO shapes during migration. Parser and serializer
  tests must cover read-old/write-new behavior until legacy support is removed.
- A list is more verbose than a flat dictionary for a single ADO link. The
  verbosity buys ordering, multiples, labels, and future metadata.
- Read-only v1 means users must edit YAML or use Obsidian to change Links until a
  later UI slice adds editing.
- Implementation slices that write migrated tasks must register those writes with
  `SelfWriteCoordinator`; otherwise `FileWatcherService` will treat lazy
  migration writes as external edits.

### Reversible?

Partially. The UI can defer or remove the Links section without changing stored
task prose. The schema commitment is harder to reverse once tasks serialize
`links:` because consumers will expect typed entries. Keeping `ado_link` parsing
as a compatibility layer makes rollback of the app behavior possible, but the
typed list should be treated as the durable canonical format after implementation
lands.

## Why this ADR exists

The skill rule for ADRs: hard to reverse + surprising without context + real
trade-off. This decision qualifies on all three:

- **Hard to reverse**: the on-disk task schema is source-of-truth vault data.
  Once `links:` is written, agents, docs, and future UI slices will depend on it.
- **Surprising without context**: future contributors may ask why a verbose list
  was chosen over a compact keyed dictionary, or why `ado_link` disappears on
  save. This ADR records the schema and migration rationale.
- **Real trade-off**: bulk migration and in-app editing are attractive, but the
  accepted v1 boundary favors a safer lazy migration and read-only UI.
