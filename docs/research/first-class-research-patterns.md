# First-class Research in Glasswork: product-pattern survey

**Access date:** 2026-08-15  
**Method:** primary product documentation only.

## Question

Should Glasswork model Research as a workflow object with its own lifecycle, or
as a curated topic surface over existing Wiki Pages?

The user's concrete example is async callbacks: one broad subject represented
across a Project page, a Concept page, Source pages, people pages, and many
driving questions. The desired experience is a central place to read that
material and explicitly ask agents to improve it, while preserving the Wiki as
the durable source of truth.

## Findings

### Personal knowledge systems

#### Obsidian

Obsidian keeps Markdown notes as the atomic content unit. Properties, tags,
wikilinks, Bookmarks, Canvas placement, and Bases are additive views or metadata
over those files. Bases create filtered table, list, card, and map views without
moving the underlying notes or creating a second content store. Lifecycle fields
are user-defined properties, not an intrinsic property of a topic.

Sources:

- [Properties](https://help.obsidian.md/properties)
- [Introduction to Bases](https://help.obsidian.md/bases)
- [Backlinks](https://help.obsidian.md/plugins/backlinks)
- [Canvas](https://help.obsidian.md/plugins/canvas)
- [Bookmarks](https://help.obsidian.md/plugins/bookmarks)

#### Heptabase

Cards are durable knowledge units stored independently of Whiteboards. A card
can appear on multiple Whiteboards, and Whiteboards organize a topic spatially
without owning or copying its cards. Tags provide a separate tabular/property
overlay. Heptabase recommends Whiteboards for connected knowledge and ideas,
while tags are better suited to archival records. Research lifecycle is not a
built-in property of a Whiteboard.

Sources:

- [Fundamental Elements](https://wiki.heptabase.com/fundamental-elements)
- [Organize knowledge and projects](https://wiki.heptabase.com/organize-knowledge-and-projects)

#### Capacities

Capacities separates typed Objects from several independent organizing
mechanisms:

- Tags provide thematic membership across object types.
- Collections are manually curated groups within one object type.
- Queries are rule-based views that update automatically.

Its documentation explicitly treats status as a property of an object type, not
as a topic/tag concern. It also provides a useful test for introducing a new
object type: the content must form a distinct group, require distinct
properties, and not be adequately expressible through queries or property
visibility.

Sources:

- [Object types](https://docs.capacities.io/reference/content-types)
- [Organizational structures](https://docs.capacities.io/reference/organizational-structures)
- [Collections](https://docs.capacities.io/reference/collections)
- [Queries](https://docs.capacities.io/reference/queries)
- [Tags](https://docs.capacities.io/reference/tags)
- [Queries versus collections](https://docs.capacities.io/faq/editing/queries-vs-collections)
- [When to create a new object type](https://docs.capacities.io/tutorials/when-to-create-new-object-type)

#### Tana Outliner

Nodes are the atomic unit. Supertags add types and fields, while Search nodes
provide live query views over those same nodes. Applying a supertag is additive
and reversible. A research-specific lifecycle would be a user-defined field,
not an intrinsic property of the organizing surface.

Sources:

- [Nodes and references](https://outliner.tana.inc/learn/features/nodes-and-references)
- [Supertags](https://outliner.tana.inc/learn/features/supertags)
- [Fields](https://outliner.tana.inc/learn/features/fields)
- [Search nodes](https://outliner.tana.inc/learn/features/search-nodes)

### AI-grounded research workspaces

#### Google Gemini Notebook

A Notebook is an isolated, user-curated collection of explicitly added sources.
The user may select a subset for a particular query. Answers and generated
Studio outputs are grounded in those sources and cite them. Imported sources
are either copies or auto-synced versions; the model does not edit the originals.

Sources:

- [Learn about Gemini Notebook](https://support.google.com/gemininotebook/answer/16164461?hl=en)
- [Create a notebook](https://support.google.com/gemininotebook/answer/16206563?hl=en)
- [Add or discover sources](https://support.google.com/gemininotebook/answer/16215270?hl=en)

#### Microsoft 365 Copilot Notebooks

A Notebook is an explicit set of references. Copilot uses only content the user
added and can access; it does not implicitly search the user's entire Microsoft
365 graph. Linked references remain live and permission-aware rather than being
copied into a second knowledge store. Answers cite the references.

Sources:

- [How Copilot Notebooks works](https://support.microsoft.com/en-us/Microsoft-365-Copilot/how-microsoft-365-copilot-notebooks-works)
- [Add references](https://support.microsoft.com/en-us/Microsoft-365-Copilot/add-references-to-your-microsoft-365-copilot-notebook)
- [Get answers and insights](https://support.microsoft.com/en-us/Microsoft-365-Copilot/get-answers-and-insights-about-your-microsoft-365-copilot-notebook)

#### Notion Research Mode

Research Mode is not a durable topic container. It performs a live search across
the workspace, connected sources, and optionally the web, with per-query source
restriction and citations. A result can be saved as an ordinary Notion page.
This provides broad freshness but lacks a persistent, intentionally curated
topic boundary.

Sources:

- [Research Mode](https://www.notion.com/help/research-mode)
- [Power deep work using Research Mode](https://www.notion.com/help/guides/power-your-deep-work-using-research-mode-in-notion)
- [Enterprise search reports](https://www.notion.com/help/guides/find-answers-and-generate-reports-with-enterprise-search)

#### Perplexity Projects

A Project is a source and instruction container for research sessions and
agentic work. It combines explicit files, connectors, and optional live search.
Actions inherit the Project's current instructions and source set, keeping agent
scope local to the topic.

Source:

- [What are Projects?](https://www.perplexity.ai/help-center/en/articles/10352961-what-are-spaces)

### Research-to-work handoff

Linear, Notion, GitHub, and Obsidian all preserve a structural distinction
between durable knowledge and actionable work:

- Linear Documents attach to Projects, Initiatives, or Issues but do not become
  Issues or inherit issue status.
- Notion connects wiki, project, and task databases through explicit Relations.
- GitHub recommends Discussions for discovery and Issues when work is ready to
  be scoped. The handoff is deliberate and reference-preserving.
- Obsidian leaves the distinction to properties and links; Bases query existing
  notes rather than promoting them into another object type.

Sources:

- Linear: [Documents](https://linear.app/docs/documents),
  [Projects](https://linear.app/docs/projects),
  [Initiatives](https://linear.app/docs/initiatives)
- Notion: [Wikis and verified pages](https://www.notion.com/help/wikis-and-verified-pages),
  [Relations and rollups](https://www.notion.com/help/relations-and-rollups)
- GitHub: [About Discussions](https://docs.github.com/en/discussions/collaborating-with-your-community-using-discussions/about-discussions),
  [Discussion best practices](https://docs.github.com/en/discussions/guides/best-practices-for-community-conversations-on-github),
  [About Issues](https://docs.github.com/en/issues/tracking-your-work-with-issues/learning-about-issues/about-issues),
  [About Projects](https://docs.github.com/en/issues/planning-and-tracking-with-projects/learning-about-projects/about-projects)

## Convergent product pattern

Despite different storage models, the products consistently separate four
concerns:

1. **Durable content** remains in its original page, card, object, node, file,
   or document.
2. **Topic membership** is additive, reversible, and often many-to-many.
3. **A topic surface** is a lens over those records, not their owner.
4. **Workflow state** belongs to projects, issues, or tasks, not to the topic
   surface.

Manual curation and live queries are also separate capabilities. Products often
support both over the same records: Collections plus Queries, Whiteboards plus
tag tables, Bookmarks plus Bases, or explicit notebook references plus
per-query source selection.

## Recommendation for Glasswork

### Canonical concept

Use **Research Topic** for the user-curated subject surfaced in Glasswork.

A Research Topic is:

- one entry on the first-class Research Page;
- an ordinary, LLM-maintained Wiki Page whose `type` is one of the Wiki's
  schema-governed knowledge types;
- grounded by a bounded, inspectable set of related Wiki Pages;
- explicitly opted into Glasswork;
- always read from the current Vault state;
- not a Task and not a workflow container.

The topic's primary Wiki Page should remain the Wikipedia-like synthesis: for
example, `wiki/concepts/arm-async-polling.md` or
`wiki/projects/async-polling-reduction.md`. Related Project, Concept, Source,
Entity, Decision, and System pages remain independent and are referenced, not
copied.

### Opt-in model

The opt-in is durable Vault metadata because it expresses a durable role of the
knowledge page, not a machine-local presentation choice:

```yaml
glasswork:
  research:
    include: [optional-wiki-page-id]
    exclude: [optional-wiki-page-id]
```

The `glasswork.research` block's presence opts the page in and leaves room for
future Research-specific metadata. It does not move the page, change its
existing `type`, or add Task status semantics.

The live Research context is the Topic plus schema-governed Wiki Pages connected
by one direct outgoing Wiki link, provenance reference, or Backlink, adjusted by
the include/exclude overrides. Traversal stops at one hop. Before starting a
Research Session the full context is visibly selected and may be narrowed for
that run without changing durable membership.

### Research Page

The Page should begin as a focused, read-first surface:

- topic list with title, Wiki type, freshness, confidence, and updated date;
- topic detail rendered through the existing Vault Markdown View;
- related Wiki Pages grouped by existing Wiki type;
- backlinks and outgoing Wiki links;
- visible source provenance and stale/expired indicators;
- active Related Work plus completed work collapsed by default;
- a secondary history action opening the Topic's Research Change Log;
- Open in Obsidian;
- explicit agent actions such as **Continue research**, **Refresh stale
  claims**, **Add sources**, and **Improve this page**;
- handoffs for **Create Task**, **Explore with Wayfinder**, and
  **Link existing work**.

Agent actions start a normal Copilot Research Session grounded in the visibly
selected context and the user's prompt. The session may investigate new primary
sources, but every durable addition follows the Wiki's provenance and
explicit-write rules. The app itself never edits synthesis prose.

Write-producing sessions append one dated summary with links to changed Wiki
Pages to `<vault>/wiki/research-logs/<topic-id>.md`. Logs are excluded from
Research context and hidden from the main synthesis. **Remove from Research**
confirms that the Topic page will remain, removes its opt-in metadata, and
permanently deletes the log. Re-adding the Topic starts history from zero.

### Relationship to Wayfinder and Tasks

Research, Wayfinder, and Tasks form a deliberate progression, not one merged
schema:

```text
Research Topic
durable understanding and open questions
        |
        | explicit "Explore as work" handoff with backlink
        v
Wayfinder map or ticket
ambiguous outcome, alternatives, decisions, decomposition
        |
        | explicit actionable slices
        v
Task
owned work with status, priority, due dates, and completion
```

A Research Topic may spawn zero, one, or many Tasks or Wayfinder tickets over
time. A clear next behavior or deliverable creates a Task directly; an important
outcome whose path or decisions remain unclear goes through Wayfinder first.
Creating or linking work leaves reciprocal references. Closing the resulting
work never archives or completes the Research Topic.

### Why this is the deeper module

The useful seam is not a generic vault browser. It is a **Research Catalog**
whose interface can answer:

- Which Wiki Pages are opted-in Research Topics?
- What current pages ground a selected topic?
- What freshness, confidence, provenance, and relationships should the Page
  display?
- What exact bounded context should an agent receive?

The implementation can hide frontmatter parsing, wikilink resolution, page-type
grouping, file watching, freshness computation, and agent-context assembly
behind that small interface. A generic file browser would expose those concerns
to the Presentation layer and provide little leverage.

## Questions delegated to a runnable prototype

The product and domain model are settled. A focused prototype should answer only
the interaction questions that are difficult to settle on paper:

1. Pane proportions and Topic-list density.
2. Whether selecting a related page replaces the synthesis pane, opens a nested
   reader, or uses a lightweight preview.
3. How the history affordance and Change Log should appear without competing
   with the synthesis.
4. How much source-selection detail can remain visible before launching a
   Research Session without making the Page feel like configuration.
5. Placement and hierarchy of Research Session actions versus Research handoff
   actions.
