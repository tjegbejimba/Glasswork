# Domain docs

Glasswork uses a single-context documentation layout. The solution contains
several .NET projects, but they implement bounded contexts within one product
domain rather than independent monorepo packages.

## Before exploring

Read these sources before proposing or implementing changes:

1. **`.github/copilot-instructions.md`** for repository-wide engineering rules,
   build constraints, and investigation workflow.
2. **`CONTEXT.md`** for the bounded contexts, ownership boundaries, and
   cross-context communication.
3. **`UBIQUITOUS_LANGUAGE.md`** for canonical terms and aliases to avoid.
4. Relevant records under **`docs/adr/`** before changing behavior or a settled
   design.

Read only the ADRs relevant to the area being changed. If a referenced domain
file does not exist, proceed without inventing a replacement; use the
`domain-modeling` skill when a real terminology or decision gap needs to be
resolved.

## Layout

```text
/
|-- .github/copilot-instructions.md
|-- CONTEXT.md
|-- UBIQUITOUS_LANGUAGE.md
|-- docs/
|   `-- adr/
|-- src/
|   |-- Glasswork.App/
|   |-- Glasswork.Core/
|   `-- Glasswork.Mcp/
`-- tests/
```

There is no `CONTEXT-MAP.md` or context-scoped `src/*/docs/adr/` hierarchy.
System-wide decisions remain in `docs/adr/`.

## Use canonical vocabulary

Use the exact terms from `UBIQUITOUS_LANGUAGE.md` in issue titles, proposals,
hypotheses, test names, code, and UI copy. Do not substitute an alias that the
glossary rejects. For example:

- **Task**, not "work item", unless referring specifically to Azure DevOps.
- **Subtask**, **Artifact**, **Link**, and **Backlink** retain their distinct
  meanings.
- **Vault** means the Obsidian vault root, not a generic repository or folder.

If a needed concept is absent, first check whether existing language already
covers it. If the gap is real, flag it and use the `domain-modeling` skill to
update the glossary or record a decision.

## Respect decisions

Surface conflicts with an existing ADR explicitly rather than silently
overriding them. State which ADR would be revisited and why before implementing
the conflicting direction.
