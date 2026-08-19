---
name: glasswork-research-session
description: 'Run a governed Glasswork Research Session from an invocation beginning "Start Glasswork Research Session:". Resolve only the selected Topic/context IDs, investigate primary sources when useful, and return durable learning through the Wiki governance entry point.'
---

# Glasswork Research Session

Use the JSON contract after `Start Glasswork Research Session:` as the complete
handoff from Glasswork. It contains:

- `topicId`: the locked Research Topic ID;
- `contextPageIds`: the exact pages selected for this run, including the Topic;
- `action`: `continue-research`, `refresh-stale-claims`, `add-sources`,
  `improve-page`, or `open-question`;
- `intent`: the selected Open Question when `action` is `open-question`;
- `wikiGovernance`: the Vault-relative governance entry point.

## Process

1. **Validate the handoff.** Parse the JSON, reject unknown actions or fields
   with unusable values, and confirm `contextPageIds` contains `topicId`. Treat
   the selected IDs as an exact boundary, not a discovery seed.

2. **Enter through governance.** Locate the user's Wiki Vault, then read the
   root file named by `wikiGovernance` before reading or changing Wiki prose.
   Follow every relevant pointer and confirmation rule it defines. The
   invocation requests research; authority to write comes from that governance.

3. **Resolve the selected pages.** Locate each selected stable ID through
   frontmatter metadata and require exactly one schema-governed Wiki Page per
   ID. Metadata-only lookup may scan page headers to resolve paths. Stop with a
   precise missing/ambiguous-ID report rather than substituting another page.

4. **Ground narrowly.** Read the Topic and only the resolved
   `contextPageIds`. Do not use unrelated Vault prose, general Vault search
   results, Research Change Logs, Tasks, or transitive Wiki links as ambient
   grounding. A newly discovered source is evidence to assess, not permission
   to absorb neighboring Vault pages.

5. **Follow the requested intent.**
   - `continue-research`: advance the Topic's most important supported claim or
     unresolved question.
   - `refresh-stale-claims`: re-check time-sensitive or low-confidence claims
     against current primary evidence.
   - `add-sources`: strengthen provenance with high-quality primary sources.
   - `improve-page`: improve the Topic synthesis while preserving its schema,
     scope, and established terminology.
   - `open-question`: investigate the exact `intent`; keep the question as Wiki
     prose rather than creating Task, Subtask, or workflow state.

6. **Investigate primary evidence.** Use authoritative specifications,
   official documentation, standards, source repositories, first-party data,
   or direct records where available. Keep citations and provenance precise.
   Distinguish what the selected Wiki pages already establish from what external
   evidence newly supports.

7. **Return durable learning through the Wiki.** Apply the governance file's
   explicit-write rules before any mutation. Make targeted edits to the Topic or
   another selected Wiki Page, and create a new governed Wiki Page only when the
   evidence warrants a durable knowledge unit. Preserve unrelated frontmatter
   and prose.

8. **Record knowledge-changing sessions.** After every governed Wiki mutation
   succeeds, call `append_research_change_log` exactly once with the locked
   `topic_id`, one concise summary of the durable knowledge changed, and the
   stable IDs of every Wiki Page changed. The tool serializes concurrent writers
   and atomically preserves prior entries. Never put the user's prompt, tool
   output, reasoning, or chat transcript in the summary. If no Wiki Page changed,
   pass an empty `changed_page_ids` collection; the tool returns a no-op and does
   not create a log.

9. **Keep membership explicit.** A durable addition joins Research context only
   when a governed Wiki relationship links it to the Topic or an explicit
   `glasswork.research.include` override adds its stable ID. Investigation,
   citation, tool retrieval, and chat mention alone do not change membership.

10. **Finish on durable state.** Report the evidence reviewed, Wiki Pages changed,
   and any remaining Open Questions. Chat history is not domain state: do not
   treat the conversation, prompt, or transcript as durable Research knowledge.
   If no governed Wiki write occurs, say that the session was read-only.

## Boundaries

- The selected context is session-local. Do not rewrite durable include/exclude
  metadata merely because the user narrowed this run.
- Research Topics have no completion, archive, or question lifecycle.
- Related Tasks and Wayfinder work retain their own lifecycle and are not
  implicit Research context.
- Research Change Logs live only at
  `wiki/research-logs/<topic-id>.md`. Do not create another history file or edit
  the log directly; `append_research_change_log` is the preserving external
  session contract.

## Change Log append contract

Call:

```text
append_research_change_log(
  topic_id: "<stable Topic ID>",
  summary: "<one concise line describing durable knowledge changed>",
  changed_page_ids: ["<stable changed Wiki Page ID>", "..."])
```

The Topic and every changed page ID must resolve uniquely to current,
schema-governed Wiki Pages. The tool owns the RFC 3339 timestamp and the single
`wiki/research-logs/<topic-id>.md` file. It serializes writers with a per-Topic
Vault lock, revalidates Research membership while holding that lock, writes a
durable same-directory temporary file, and atomically replaces the prior log so
existing entries survive. An empty `changed_page_ids` list returns
`no_knowledge_changes` without creating a file. A malformed existing log returns
`malformed_log` without overwriting recoverable history.
