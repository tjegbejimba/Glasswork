namespace Glasswork.Core.Models;

/// <summary>
/// A markdown work-product attached to a task, typically agent-produced
/// (plan, design, investigation, draft, summary). Stored as a *.md file in
/// &lt;vault&gt;/wiki/todo/&lt;task-id&gt;.artifacts/. Read-only in the app.
/// </summary>
public sealed record Artifact(
    string Path,
    string Title,
    DateTime ModifiedUtc,
    string Body);
