using System;

namespace Glasswork.Core.Feedback;

/// <summary>
/// Snapshot of UI state at the moment the user opened the feedback dialog.
/// Captured by the App layer (page name, active task) and the runtime (versions, OS)
/// and rendered into the issue body by <see cref="FeedbackBodyFormatter"/> so triage
/// has the context the user forgot to mention.
/// </summary>
public sealed record FeedbackContext(
    string? PageName,
    string? ActiveTaskId,
    string AppVersion,
    string OsDescription,
    string RuntimeVersion,
    DateTimeOffset CapturedAtUtc);
