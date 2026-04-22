using System;

namespace Glasswork.Core.Models;

/// <summary>
/// Persisted shape for a resolved ADO parent work-item title. Cached app-locally
/// (via <see cref="Services.IUiStateService"/>, key prefix <c>ado.parent.title.</c>)
/// so backlog group headers render with the resolved title from the first frame
/// after launch instead of flickering the bare numeric ID. <see cref="ResolvedAt"/>
/// is UTC and used to enforce the 30-day TTL on hydration.
/// </summary>
public sealed record AdoParentTitleEntry(string Title, DateTime ResolvedAt);
