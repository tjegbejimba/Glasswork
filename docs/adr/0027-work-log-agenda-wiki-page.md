# Work Log Agenda presents one configured Wiki Page

Glasswork broadens Work Log from completed-work history to weekly review and
preparation by adding an optional Agenda tab. Agenda renders exactly one
schema-governed Wiki Page as read-only Markdown through `VaultMarkdownView`;
the user-facing enable preference and selected stable Wiki Page ID live in UI
State, while the page and its prose remain Vault-owned. This keeps recurring
preparation beside completed-work review without misclassifying the page as a
Research Topic, adding a second editor, or turning Glasswork into a general
Wiki browser.

## Consequences

- Agenda is off by default and is enabled through Settings.
- Page selection and replacement happen inside Agenda through a searchable
  chooser over schema-governed Wiki Pages.
- Wiki Page links open in Obsidian and Task links open Task Detail.
- A missing or invalid selected page produces an explicit repair state without
  clearing the stored selection.
