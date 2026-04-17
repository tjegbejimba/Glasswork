# Glasswork

A WinUI 3 desktop to-do app that uses your Obsidian vault as the backing store. Bridges Azure DevOps work items and personal task breakdowns with full context linking.

## Features

- **Obsidian-backed storage** — Tasks are markdown files with YAML frontmatter, browseable in Obsidian
- **ADO integration** — Pull work items on-demand, break them into actionable tasks
- **My Day** — Daily planning view with smart carryover suggestions
- **Agent-friendly** — `_index.md` and `_today.md` provide machine-readable task state
- **Work log** — Auto-generated weekly log from completed tasks for connects season

## Getting Started

```bash
dotnet restore
dotnet build
dotnet run
```

## Architecture

Built with WinUI 3 / .NET / C# using the MVVM pattern (CommunityToolkit.Mvvm). Tasks are stored as markdown files in your Obsidian vault's `wiki/todo/` directory.

## License

Personal project — not licensed for distribution.
