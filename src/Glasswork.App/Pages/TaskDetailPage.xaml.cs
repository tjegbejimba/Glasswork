using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glasswork.Controls;
using Glasswork.Core.Markdown;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;
using Glasswork.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Glasswork.Pages;

public sealed partial class TaskDetailPage : Page
{
    public GlassworkTask Task { get; private set; } = new();

    private bool _isLoading;
    private bool _isNavigated;
    private bool _isDeletingTask;
    private bool _suppressNextNotesSave;
    private NotesEditController _notesEdit = new(string.Empty);
    private readonly TaskEditSaveController _saveController;
    private string _pendingDiskNotes = string.Empty;
    private ParentLinkResolution? _parentResolution;

    /// <summary>
    /// Per-artifact expand state (keyed by absolute Path, case-insensitive),
    /// preserved across watcher-driven <see cref="BindArtifacts"/> refreshes so a
    /// background file change does not collapse a row the user opened. Cleared
    /// when the displayed task changes; stale keys are pruned on each rebind.
    /// The live HTML preview is deliberately NOT preserved across refreshes.
    /// </summary>
    private readonly Dictionary<string, bool> _artifactExpandState = new(StringComparer.OrdinalIgnoreCase);
    private string? _artifactsTaskId;
    private EventHandler<object>? _deferredArtifactBindHandler;
    private int _artifactBindGeneration;

    public TaskDetailPage()
    {
        _saveController = new TaskEditSaveController(App.Vault);
        InitializeComponent();
        // Always re-create this page on navigation so Reload (which re-navigates
        // with a fresh GlassworkTask) cannot be deduped to the cached instance.
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isNavigated = true;
        App.ObsidianLauncher.NotInstalled += OnObsidianNotInstalled;
        if (e.Parameter is TaskDetailNavigation nav)
        {
            // Navigated from My Day's "flagged subtasks" section — display the parent task
            // (FocusSubtaskTitle is currently informational; UI affordance for scrolling could
            // be added later).
            if (App.Watcher is not null)
            {
                App.Watcher.TaskFileChanged += OnTaskFileChangedExternally;
                App.Watcher.TaskFileChange += OnAnyTaskFileChange;
            }
            App.ArtifactChangedExternally += OnArtifactChangedExternally;
            App.BacklinksChangedExternally += OnBacklinksChangedExternally;
            ApplyTask(nav.Task);
            return;
        }
        if (e.Parameter is GlassworkTask task)
        {
            if (App.Watcher is not null)
            {
                App.Watcher.TaskFileChanged += OnTaskFileChangedExternally;
                App.Watcher.TaskFileChange += OnAnyTaskFileChange;
            }
            App.ArtifactChangedExternally += OnArtifactChangedExternally;
            App.BacklinksChangedExternally += OnBacklinksChangedExternally;
            ApplyTask(task);
        }
    }

    private void ApplyTask(GlassworkTask task)
    {
        _isLoading = true;
        Task = task;
        // Refresh compiled x:Bind expressions (Title, Description, Notes use TwoWay bindings
        // that captured the previous Task object at initialization; Update() re-roots them on
        // the new task so the UI reflects the agent's changes and subsequent saves are correct).
        Bindings.Update();
        App.ActiveTask.ActiveTaskId = task.Id;

        // Set combo boxes to match task state
        SetComboByTag(StatusBox, task.Status);
        SetComboByTag(PriorityBox, task.Priority);
        StatusBox.IsEnabled = !task.IsBlocked && !task.IsCancelled;
        BlockedStatusText.Visibility = task.IsBlocked ? Visibility.Visible : Visibility.Collapsed;
        BlockedStatusText.Text = task.IsBlocked
            ? (task.NeedsBlockerDetails ? "Needs blocker details" : $"Blocked: {task.BlockedReason}")
            : string.Empty;
        BlockTaskButton.Visibility = !task.IsBlocked ? Visibility.Visible : Visibility.Collapsed;
        EditBlockerButton.Visibility = task.IsBlocked && !task.NeedsBlockerDetails ? Visibility.Visible : Visibility.Collapsed;
        RepairBlockedButton.Visibility = task.IsBlocked && task.NeedsBlockerDetails ? Visibility.Visible : Visibility.Collapsed;
        ResumeBlockedButton.Visibility = task.IsBlocked && !task.NeedsBlockerDetails ? Visibility.Visible : Visibility.Collapsed;
        MarkBlockedDoneButton.Visibility = task.IsBlocked && !task.NeedsBlockerDetails ? Visibility.Visible : Visibility.Collapsed;
        CancelTaskButton.Visibility = task.Status is (
            GlassworkTask.Statuses.Todo
            or GlassworkTask.Statuses.InProgress
            or GlassworkTask.Statuses.Blocked)
                ? Visibility.Visible
                : Visibility.Collapsed;
        var isReadOnly = task.IsCancelled;
        TitleBox.IsReadOnly = isReadOnly;
        PriorityBox.IsEnabled = !isReadOnly;
        DueDatePicker.IsEnabled = !isReadOnly;
        EditAdoButton.IsEnabled = !isReadOnly;
        EditParentButton.IsEnabled = !isReadOnly;
        ActiveSubtaskList.IsEnabled = !isReadOnly;
        CompletedSubtaskList.IsEnabled = !isReadOnly;
        AddSubtaskBox.IsEnabled = !isReadOnly;
        AddSubtaskButton.IsEnabled = !isReadOnly;
        DescriptionBox.IsReadOnly = isReadOnly;
        NotesEditButton.IsEnabled = !isReadOnly;
        AddLinkButton.IsEnabled = !isReadOnly;
        LifecycleActions.Visibility = isReadOnly
            ? Visibility.Collapsed
            : Visibility.Visible;

        DueDatePicker.Date = task.Due.HasValue
            ? new DateTimeOffset(task.Due.Value)
            : (DateTimeOffset?)null;

        BindSubtasks(task.Subtasks);
        BindRelated(task.RelatedLinks);
        ArtifactsSection.Visibility = Visibility.Collapsed;
        ArtifactsList.ItemsSource = null;
        BindLinks(task.Links);
        BindChildren(task.Id);
        BindBacklinks(task.Id);

        CreatedText.Text = $"Created: {task.Created:yyyy-MM-dd}";
        CompletedText.Text = task.Status == GlassworkTask.Statuses.Done && task.CompletedAt.HasValue
            ? $"Completed: {task.CompletedAt.Value:yyyy-MM-dd HH:mm}"
            : "";
        CancelledText.Text = task.IsCancelled
            ? $"Cancelled: {task.CancelledAt?.ToLocalTime():yyyy-MM-dd HH:mm} - {task.CancellationReason}"
            : "";
        IdText.Text = $"ID: {task.Id}";

        if (task.AdoLink.HasValue)
        {
            AdoLabel.Visibility = Visibility.Visible;
            AdoLinkButton.Visibility = Visibility.Visible;
            AdoTitleRun.Text = $"#{task.AdoLink} \u2014 {task.AdoTitle ?? "linked"}";
            EditAdoButton.Content = "Edit ADO link";
        }
        else
        {
            AdoLabel.Visibility = Visibility.Collapsed;
            AdoLinkButton.Visibility = Visibility.Collapsed;
            AdoTitleRun.Text = string.Empty;
            EditAdoButton.Content = "Link ADO work item";
        }

        ApplyParent(task);

        _notesEdit = new NotesEditController(task.Notes);
        ApplyNotesMode(NotesEditMode.Read);
        // Fresh task → discard any stale conflict banner state from a previous task.
        NotesConflictBanner.IsOpen = false;
        ReloadBanner.IsOpen = false;
        _pendingDiskNotes = string.Empty;

        _isLoading = false;
        ScheduleInitialArtifactBind(task.Id);
    }

    private void ScheduleInitialArtifactBind(string taskId)
    {
        CancelDeferredArtifactBind();
        var generation = ++_artifactBindGeneration;
        _deferredArtifactBindHandler = (_, _) =>
        {
            CancelDeferredArtifactBind();
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (!_isNavigated
                        || generation != _artifactBindGeneration
                        || !string.Equals(Task?.Id, taskId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    BindArtifacts(taskId);
                });
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _deferredArtifactBindHandler;
    }

    private void CancelDeferredArtifactBind()
    {
        if (_deferredArtifactBindHandler is null)
            return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _deferredArtifactBindHandler;
        _deferredArtifactBindHandler = null;
    }

    private void ApplyNotesMode(NotesEditMode mode)
    {
        // Bypass VisualStateManager: the VisualStateGroups in XAML are attached
        // to the inner NotesContent grid, not to a child of `this` (the Page),
        // so VisualStateManager.GoToState(this, ...) silently no-ops. Direct
        // visibility writes on the two named children are simpler and reliable.
        // See issue #108.
        if (mode == NotesEditMode.Read)
        {
            NotesReadView.Markdown = Task.Notes ?? string.Empty;
            NotesReadView.Visibility = Visibility.Visible;
            NotesBox.Visibility = Visibility.Collapsed;
            var hasContent = !string.IsNullOrWhiteSpace(Task.Notes);
            NotesEmptyHint.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
            NotesEditIcon.Glyph = "\uE70F";
            ToolTipService.SetToolTip(NotesEditButton, "Edit Notes (Ctrl+E)");
        }
        else
        {
            NotesReadView.Visibility = Visibility.Collapsed;
            NotesBox.Visibility = Visibility.Visible;
            NotesEmptyHint.Visibility = Visibility.Collapsed;
            NotesEditIcon.Glyph = "\uE73E";
            ToolTipService.SetToolTip(NotesEditButton, "Done (Ctrl+E)");
            // Force layout so the TextBox is in the tree before we focus it.
            NotesContent.UpdateLayout();
            NotesBox.Focus(FocusState.Programmatic);
            NotesBox.SelectionStart = NotesBox.Text?.Length ?? 0;
        }
    }

    private async void NotesEditToggle_Click(object sender, RoutedEventArgs e) => await ToggleNotesMode();

    private async void NotesEditAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        await ToggleNotesMode();
        args.Handled = true;
    }

    private async Task ToggleNotesMode()
    {
        if (_isLoading || Task.IsCancelled) return;
        if (_notesEdit.Mode == NotesEditMode.Read)
        {
            _notesEdit.EnterEdit();
            ApplyNotesMode(NotesEditMode.Edit);
        }
        else
        {
            // Done: flush the TwoWay binding into Task.Notes, then save.
            Task.Notes = NotesBox.Text ?? string.Empty;
            _notesEdit.UpdateBuffer(Task.Notes);
            if (await SaveAsync())
            {
                _notesEdit.Done();
                ApplyNotesMode(NotesEditMode.Read);
            }
        }
    }

    private void NotesBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        if (_notesEdit.Mode != NotesEditMode.Edit) return;

        // Restore baseline before LostFocus fires (which would otherwise persist the buffer).
        var baseline = _notesEdit.Cancel();
        _suppressNextNotesSave = true;
        Task.Notes = baseline;
        NotesBox.Text = baseline;
        ApplyNotesMode(NotesEditMode.Read);
        e.Handled = true;
    }

    private void OnNotesMarkdownLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not VaultMarkdownView view) return;
        view.WikiLinkResolver ??= VaultPageHelper.BuildWikiLinkResolver();
        view.LinkClicked -= OnArtifactLinkClicked;
        view.LinkClicked += OnArtifactLinkClicked;
    }

    private void OnNotesMarkdownUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not VaultMarkdownView view) return;
        view.LinkClicked -= OnArtifactLinkClicked;
    }

    private void ApplyParent(GlassworkTask task)
    {
        var p = task.Parent?.Trim();
        if (string.IsNullOrEmpty(p))
        {
            ParentLabel.Visibility = Visibility.Collapsed;
            ParentLinkButton.Visibility = Visibility.Collapsed;
            ParentTextRun.Text = string.Empty;
            EditParentButton.Content = "Set parent";
            _parentResolution = null;
            return;
        }

        ParentLabel.Visibility = Visibility.Visible;
        ParentTextRun.Text = p;
        EditParentButton.Content = "Edit parent";

        // Classify parent to determine link type
        var baseUrl = (App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty).Trim();
        var classifier = new ParentLinkClassifier(App.Index);
        _parentResolution = classifier.Classify(p, baseUrl);

        // Update button visibility and content based on resolution
        if (_parentResolution.Type == ParentLinkResolution.ResolutionType.InAppTask)
        {
            ParentLinkButton.Visibility = Visibility.Visible;
            ParentLinkButton.Content = "Open task";
        }
        else if (_parentResolution.Type == ParentLinkResolution.ResolutionType.AdoUrl)
        {
            ParentLinkButton.Visibility = Visibility.Visible;
            ParentLinkButton.Content = "Open in ADO";
        }
        else
        {
            ParentLinkButton.Visibility = Visibility.Collapsed;
        }
    }

    private void BindSubtasks(IList<SubTask> subtasks)
    {
        var active = subtasks.Where(s => !s.IsEffectivelyDone).ToList();
        var completed = subtasks.Where(s => s.IsEffectivelyDone).ToList();

        ActiveSubtaskList.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<SubTask>(active);
        CompletedSubtaskList.ItemsSource = completed;

        if (completed.Count > 0)
        {
            CompletedExpander.Visibility = Visibility.Visible;
            CompletedHeader.Text = $"Completed ({completed.Count})";
        }
        else
        {
            CompletedExpander.Visibility = Visibility.Collapsed;
        }
    }

    private void BindArtifacts(string taskId)
    {
        // Reset preserved expand state when the displayed task changes.
        if (!string.Equals(_artifactsTaskId, taskId, StringComparison.OrdinalIgnoreCase))
        {
            _artifactExpandState.Clear();
            _artifactsTaskId = taskId;
        }

        IReadOnlyList<Artifact> artifacts;
        try
        {
            artifacts = App.Artifacts.Load(taskId);
        }
        catch
        {
            // Artifact loading is best-effort — never block the task view.
            artifacts = Array.Empty<Artifact>();
        }

        if (artifacts.Count == 0)
        {
            ArtifactsSection.Visibility = Visibility.Collapsed;
            ArtifactsList.ItemsSource = null;
            return;
        }

        var rows = ArtifactRow.Project(artifacts, DateTime.UtcNow);

        // Prune expand state for artifacts that no longer exist, then apply any
        // user-set state on top of the projection's size-bounded auto-expand default.
        var livePaths = new HashSet<string>(rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _artifactExpandState.Keys.Where(k => !livePaths.Contains(k)).ToList())
        {
            _artifactExpandState.Remove(stale);
        }

        var projected = rows
            .Select(r => _artifactExpandState.TryGetValue(r.Path, out var expanded)
                ? r with { IsExpanded = expanded }
                : r)
            .ToList();

        ArtifactsSection.Visibility = Visibility.Visible;
        ArtifactsList.ItemsSource = projected;
    }

    private void OnArtifactExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.DataContext is ArtifactRow row)
        {
            _artifactExpandState[row.Path] = true;
        }
    }

    private void OnArtifactCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        if (sender.DataContext is ArtifactRow row)
        {
            _artifactExpandState[row.Path] = false;
        }
    }


    private void BindChildren(string taskId)
    {
        IReadOnlyList<GlassworkTask> children;
        try
        {
            children = App.Index?.GetChildren(taskId) ?? Array.Empty<GlassworkTask>();
        }
        catch
        {
            // Children lookup is best-effort — never block the task view.
            children = Array.Empty<GlassworkTask>();
        }

        if (children.Count == 0)
        {
            ChildrenSection.Visibility = Visibility.Collapsed;
            ChildrenList.ItemsSource = null;
            return;
        }

        ChildrenSection.Visibility = Visibility.Visible;
        ChildrenHeader.Text = $"Children ({children.Count})";
        ChildrenList.ItemsSource = ChildRow.Project(children);
    }

    private void Child_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ChildRow row) return;
        var child = App.Index?.ById(row.Id);
        if (child is null) return;
        Frame.Navigate(typeof(TaskDetailPage), child);
    }

    private void BindBacklinks(string taskId)
    {
        IReadOnlyList<Backlink> backlinks;
        try
        {
            backlinks = App.BacklinkIndex?.GetBacklinks(taskId) ?? Array.Empty<Backlink>();
        }
        catch
        {
            // Backlink lookup is best-effort — never block the task view.
            backlinks = Array.Empty<Backlink>();
        }

        if (backlinks.Count == 0)
        {
            BacklinksSection.Visibility = Visibility.Collapsed;
            BacklinksList.ItemsSource = null;
            return;
        }

        BacklinksSection.Visibility = Visibility.Visible;
        BacklinksHeader.Text = $"Backlinks ({backlinks.Count})";
        BacklinksList.ItemsSource = BacklinkRow.Project(backlinks);
    }

    private async void Backlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not BacklinkRow row) return;
        var vaultRelative = ToVaultRelativePath(row.Path);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    private void BindLinks(IList<TaskLink> links)
    {
        // Section is always visible so the Add link button is always accessible.
        LinksList.ItemsSource = links.Count > 0 ? LinkRow.Project(links) : null;
    }

    private async void LinkRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not LinkRow row) return;

        // Retrieve ADO base URL from persistent UI state
        var adoBaseUrl = App.UiState?.Get<string>(App.AdoBaseUrlKey) ?? string.Empty;

        // Resolve the URI (can return null for malformed links)
        var resolved = LinkUriPolicy.Resolve(row.Source, adoBaseUrl);
        if (resolved is null) return; // Malformed link - no-op click

        // Agent-supplied links are untrusted per ADR 0009; run through security boundary
        if (ArtifactLinkPolicy.Decide(resolved.ToString()) == ArtifactLinkPolicy.Decision.Block)
        {
            // TODO (future): show warning dialog explaining the block
            return;
        }

        // Launch the external link
        await Launcher.LaunchUriAsync(resolved);
    }

    // Friendly type options for the Add link dialog: (display label, schema type string).
    private static readonly (string Display, string Type)[] LinkTypeOptions =
    [
        ("ADO work item", TaskLink.Types.Ado),
        ("GitHub / ADO PR", TaskLink.Types.Pr),
        ("ICM incident", TaskLink.Types.Incident),
        ("Doc", TaskLink.Types.Doc),
        ("Build", TaskLink.Types.Build),
        ("Other", TaskLink.Types.Other),
    ];

    private async void AddLink_Click(object sender, RoutedEventArgs e)
    {
        if (Task.IsCancelled) return;
        var typeBox = new ComboBox
        {
            Header = "Type",
            MinWidth = 160,
            ItemsSource = LinkTypeOptions.Select(o => o.Display).ToArray(),
            SelectedIndex = 0, // default to ADO (most common)
        };
        var valueBox = new TextBox
        {
            Header = "Value (URL or identifier)",
            PlaceholderText = "e.g. https://... or 12345 or ICM 965114",
            Margin = new Thickness(0, 12, 0, 0),
        };
        var labelBox = new TextBox
        {
            Header = "Label (optional display name)",
            PlaceholderText = "Short name shown in the UI",
            Margin = new Thickness(0, 12, 0, 0),
        };
        var warning = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x3E, 0x1C)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel { MinWidth = 380 };
        panel.Children.Add(typeBox);
        panel.Children.Add(valueBox);
        panel.Children.Add(labelBox);
        panel.Children.Add(warning);

        // Auto-detect type from the URL the user pastes, unless they've manually
        // picked a non-default type.
        bool userOverrodeType = false;
        typeBox.SelectionChanged += (_, __) => userOverrodeType = true;
        valueBox.TextChanged += (_, __) =>
        {
            warning.Visibility = Visibility.Collapsed;
            if (userOverrodeType) return;
            var detected = DetectLinkType(valueBox.Text);
            if (detected is null) return;
            var idx = Array.FindIndex(LinkTypeOptions, o => o.Type == detected);
            if (idx < 0 || idx == typeBox.SelectedIndex) return;
            // SelectionChanged fires here too; suppress the override flag.
            typeBox.SelectedIndex = idx;
            userOverrodeType = false;
        };

        var dialog = new ContentDialog
        {
            Title = "Add link",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);

        // Validate inside the deferral so a bad value keeps the dialog open.
        var adoBaseUrl = App.UiState?.Get<string>(App.AdoBaseUrlKey) ?? string.Empty;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var v = valueBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(v))
            {
                warning.Text = "Enter a URL or identifier.";
                warning.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
            var idx = typeBox.SelectedIndex;
            if (idx < 0) idx = 0;
            var t = LinkTypeOptions[idx].Type;
            var probe = new TaskLink { Type = t, Value = v };
            if (LinkUriPolicy.Resolve(probe, adoBaseUrl) is null)
            {
                warning.Text = $"This doesn't look like a valid link for type '{LinkTypeOptions[idx].Display}'. " +
                               "Check the value (URL or identifier) and try again, or pick a different type.";
                warning.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var selectedIdx = typeBox.SelectedIndex < 0 ? 0 : typeBox.SelectedIndex;
        var type = LinkTypeOptions[selectedIdx].Type;
        var value = valueBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(value)) return;
        var label = string.IsNullOrWhiteSpace(labelBox.Text) ? null : labelBox.Text.Trim();

        var newLink = new TaskLink { Type = type, Value = value, Label = label };
        var updatedLinks = Task.Links.Append(newLink).ToList();
        App.Vault.SetLinks(Task.Id, updatedLinks);
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    /// <summary>
    /// Heuristic URL → TaskLink.Types mapping for the Add link dialog. Returns
    /// null if the value is empty or doesn't match any known pattern.
    /// </summary>
    private static string? DetectLinkType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();

        // Bare ICM identifier like "ICM 123456"
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^ICM\s+\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return TaskLink.Types.Incident;

        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (host.Contains("portal.microsofticm.com")) return TaskLink.Types.Incident;
        if (host == "github.com" && path.Contains("/pull/")) return TaskLink.Types.Pr;
        if (host.Contains("dev.azure.com") || host.EndsWith(".visualstudio.com"))
        {
            if (path.Contains("/pullrequest")) return TaskLink.Types.Pr;
            if (path.Contains("/_workitems")) return TaskLink.Types.Ado;
            if (path.Contains("/_build")) return TaskLink.Types.Build;
        }
        return null;
    }

    private void LinkMore_Click(object sender, RoutedEventArgs e)
    {
        if (Task.IsCancelled) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not LinkRow row) return;

        var menu = new MenuFlyout();

        var deleteItem = new MenuFlyoutItem { Text = "Delete" };
        deleteItem.Click += (_, __) =>
        {
            // Use ReferenceEquals to remove only the exact clicked instance
            // (guards against duplicate links with identical values).
            var updatedLinks = Task.Links.Where(l => !ReferenceEquals(l, row.Source)).ToList();
            App.Vault.SetLinks(Task.Id, updatedLinks);
            var reloaded = App.Vault.Load(Task.Id);
            if (reloaded is not null) ApplyTask(reloaded);

        };
        menu.Items.Add(deleteItem);

        menu.ShowAt(fe);
    }


    private void BindRelated(IList<RelatedLink> links)
    {
        if (links.Count == 0)
        {
            RelatedSection.Visibility = Visibility.Collapsed;
            RelatedList.ItemsSource = null;
            return;
        }

        // wiki root = parent of the todo/ vault directory (e.g. ~/Wiki/wiki/).
        // Slugs in [[..]] are paths relative to this root.
        var wikiRoot = Path.GetDirectoryName(App.Vault.VaultPath) ?? App.Vault.VaultPath;
        var hydrated = new WikiLinkHydrator().Hydrate(links, wikiRoot);
        RelatedList.ItemsSource = hydrated;
        RelatedSection.Visibility = Visibility.Visible;
    }

    private async void RelatedLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not HydratedRelatedLink link) return;
        var normalizedTopicSlug = link.Slug.Trim().Replace('\\', '/');
        var aliasIndex = normalizedTopicSlug.IndexOf('|');
        if (aliasIndex >= 0)
            normalizedTopicSlug = normalizedTopicSlug[..aliasIndex];
        var anchorIndex = normalizedTopicSlug.IndexOf('#');
        if (anchorIndex >= 0)
            normalizedTopicSlug = normalizedTopicSlug[..anchorIndex];
        if (normalizedTopicSlug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            normalizedTopicSlug = normalizedTopicSlug[..^3];
        var topicPath = "wiki/" + normalizedTopicSlug.Trim('/') + ".md";
        var researchTopic = App.Research.Capture().Topics.FirstOrDefault(topic =>
            string.Equals(
                topic.VaultRelativePath,
                topicPath,
                StringComparison.OrdinalIgnoreCase));
        if (researchTopic is not null)
        {
            (App.MainWindow as MainWindow)?.NavigateTo(
                new GlassworkUri.ResearchTopic(researchTopic.Id));
            return;
        }
        var wikiRoot = Path.GetDirectoryName(App.Vault.VaultPath) ?? App.Vault.VaultPath;
        var absolutePath = Path.Combine(wikiRoot, link.Slug.Replace('/', Path.DirectorySeparatorChar));
        var vaultRelative = ToVaultRelativePath(absolutePath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isNavigated = false;
        _artifactBindGeneration++;
        CancelDeferredArtifactBind();
        base.OnNavigatedFrom(e);
        if (App.Watcher is not null)
        {
            App.Watcher.TaskFileChanged -= OnTaskFileChangedExternally;
            App.Watcher.TaskFileChange -= OnAnyTaskFileChange;
        }
        App.ArtifactChangedExternally -= OnArtifactChangedExternally;
        App.BacklinksChangedExternally -= OnBacklinksChangedExternally;
        App.ObsidianLauncher.NotInstalled -= OnObsidianNotInstalled;
        App.HtmlPreview.ReleaseAll();
        App.ActiveTask.Clear();
    }

    private void OnObsidianNotInstalled(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ObsidianInstallBanner.IsOpen = true);
    }

    private void OnTaskFileChangedExternally(object? sender, string fileName)
    {
        if (!App.ActiveTask.IsActive(fileName)) return;
        // Watcher fires on a thread-pool thread; marshal to UI thread before
        // touching the model or any banners. The watcher is filtered through
        // SelfWriteCoordinator, so this only fires for external edits (Obsidian, agents).
        DispatcherQueue.TryEnqueue(HandleExternalFileChange);
    }

    private void OnAnyTaskFileChange(object? sender, TaskFileChange change)
    {
        // Refresh children list on any task file change. This catches:
        // - New tasks created with parent = current task id (including MCP/agent writes)
        // - Existing tasks changing their parent field to/from current task id
        // - Child tasks being deleted
        // Uses TaskFileChange (not TaskFileChanged) so agent/MCP writes trigger refresh.
        // Slightly inefficient (refreshes on all changes) but simple and safe.
        var id = Task?.Id;
        if (string.IsNullOrEmpty(id)) return;
        var currentFileName = $"{id}.md";
        var affectsCurrentTask = string.Equals(
                change.NewFileName,
                currentFileName,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                change.OldFileName,
                currentFileName,
                StringComparison.OrdinalIgnoreCase);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (affectsCurrentTask)
            {
                var fresh = App.Vault.Load(id);
                if (fresh?.IsCancelled == true)
                {
                    ReloadBanner.IsOpen = false;
                    NotesConflictBanner.IsOpen = false;
                    ApplyTask(fresh);
                    return;
                }
            }
            BindChildren(id);
        });
    }

    private void HandleExternalFileChange()
    {
        var task = Task;
        if (task is null) return;
        var id = task.Id;
        if (string.IsNullOrEmpty(id)) return;

        // Reload to compare. We never blanket-replace Task here — that would
        // clobber unsaved Title/Description/etc. edits.
        var fresh = App.Vault.Load(id);
        if (fresh is null)
        {
            // File may have been deleted; fall back to the legacy banner.
            ReloadBanner.IsOpen = true;
            return;
        }
        if (fresh.IsCancelled)
        {
            ReloadBanner.IsOpen = false;
            NotesConflictBanner.IsOpen = false;
            ApplyTask(fresh);
            return;
        }

        var newDiskNotes = fresh.Notes ?? string.Empty;
        var classification = _notesEdit.ClassifyExternalChange(newDiskNotes);

        switch (classification)
        {
            case NotesExternalChangeAction.SilentRefresh:
                _notesEdit.ApplySilentRefresh(newDiskNotes);
                task.Notes = newDiskNotes;
                if (_notesEdit.Mode == NotesEditMode.Edit)
                    NotesBox.Text = newDiskNotes;
                else
                    NotesReadView.Markdown = newDiskNotes;
                // Spec (M8): silent — no banner. We accept that a coincident
                // change to a non-Notes field will not surface its own banner.
                // Agent edits in practice target Notes; non-Notes external
                // edits remain covered by the Ignore branch below.
                break;

            case NotesExternalChangeAction.Conflict:
                _pendingDiskNotes = newDiskNotes;
                NotesConflictBanner.IsOpen = true;
                break;

            case NotesExternalChangeAction.Ignore:
            default:
                // Notes unchanged on disk; whatever differs is non-Notes
                // (Title, Status, Subtasks, …). Surface the legacy banner so
                // the user can choose Reload vs Keep my version.
                ReloadBanner.IsOpen = true;
                break;
        }
    }

    private void OnArtifactChangedExternally(object? sender, ArtifactChangedEventArgs e)
    {
        // Refresh artifacts ONLY for the currently-displayed task. Never reload
        // the task model — that would clobber unsaved Notes/Description edits.
        if (!string.Equals(e.TaskId, Task?.Id, StringComparison.OrdinalIgnoreCase)) return;
        DispatcherQueue.TryEnqueue(() => BindArtifacts(e.TaskId));
    }

    private void OnBacklinksChangedExternally(object? sender, BacklinksChangedEventArgs e)
    {
        // Refresh the Backlinks section only when the current task is in the
        // affected set. Never reload the task model — same Notes/Description
        // protection rule as the artifact watcher.
        var id = Task?.Id;
        if (string.IsNullOrEmpty(id)) return;
        if (!e.AffectedTaskIds.Contains(id, StringComparer.Ordinal)) return;
        DispatcherQueue.TryEnqueue(() => BindBacklinks(id));
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        ReloadBanner.IsOpen = false;
        var fresh = App.Vault.Load(Task.Id);
        if (fresh is not null)
        {
            // Re-bind in place. Frame.Navigate(typeof(TaskDetailPage), ...) is unreliable
            // here because the frame may dedupe a navigation to the currently-displayed
            // page type — leaving stale field state on screen.
            ApplyTask(fresh);
        }
    }

    private async void KeepMine_Click(object sender, RoutedEventArgs e)
    {
        if (await SaveAsync(overwrite: true))
            ReloadBanner.IsOpen = false;
    }

    private void NotesConflictDiscard_Click(object sender, RoutedEventArgs e)
    {
        // Discard mine and reload: replace TextBox + baseline with disk and
        // transition back to read mode.
        var disk = _pendingDiskNotes;
        _suppressNextNotesSave = true;
        _notesEdit.ApplyDiscardAndReload(disk);
        Task.Notes = disk;
        NotesBox.Text = disk;
        ApplyNotesMode(NotesEditMode.Read);
        NotesConflictBanner.IsOpen = false;
        _pendingDiskNotes = string.Empty;
    }

    private async void NotesConflictKeep_Click(object sender, RoutedEventArgs e)
    {
        // The user's buffer and edit mode are preserved while the explicit
        // overwrite advances the resource revision through the save controller.
        _notesEdit.ApplyKeepAndOverwrite(_pendingDiskNotes);
        if (await SaveAsync(overwrite: true))
        {
            _notesEdit.OnExternalSave(Task.Notes);
            NotesConflictBanner.IsOpen = false;
            _pendingDiskNotes = string.Empty;
        }
    }

    private async void NotesConflictOpenObsidian_Click(object sender, RoutedEventArgs e)
    {
        // Build the vault-relative path to the active task file and let the
        // user resolve in Obsidian. Banner stays open until they decide.
        var taskPath = Path.Combine(App.Vault.VaultPath, $"{Task.Id}.md");
        var vaultRelative = ToVaultRelativePath(taskPath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    private async void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        // Button.Click runs after focus leaves the editor. Let the conflict
        // action decide between Reload and Keep before any stale autosave.
        if (ReloadBanner.IsOpen || NotesConflictBanner.IsOpen) return;
        if ((TextBox)sender == NotesBox && _suppressNextNotesSave)
        {
            _suppressNextNotesSave = false;
            return;
        }
        await SaveAsync();
    }

    private async void Status_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (StatusBox.SelectedItem is ComboBoxItem item)
        {
            var status = item.Tag?.ToString() ?? "todo";
            try
            {
                if (status == GlassworkTask.Statuses.Blocked)
                {
                    var reason = await PromptBlockedReasonAsync("Mark blocked", null);
                    if (reason is null)
                    {
                        RestoreStatusSelection();
                        return;
                    }

                    App.Tasks.MarkBlocked(Task, reason);
                }
                else
                {
                    App.Tasks.SetStatus(Task, status);
                }

                ReloadTaskFromVault();
            }
            catch (Exception ex)
            {
                RestoreStatusSelection();
                await ShowOperationErrorAsync("Unable to change status", ex.Message);
            }
        }
    }

    private async void Priority_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (PriorityBox.SelectedItem is ComboBoxItem item)
        {
            Task.Priority = item.Tag?.ToString() ?? "medium";
            await SaveAsync();
        }
    }

    private async void DueDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_isLoading) return;
        Task.Due = args.NewDate?.DateTime;
        await SaveAsync();
    }

    private void Subtask_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        if (sender is CheckBox cb && cb.DataContext is SubTask sub)
        {
            App.Vault.UpdateSubtaskCheckbox(Task.Id, sub.Text, cb.IsChecked == true);
            // Re-partition Active vs Completed on next frame so the toggled row
            // moves to the right list without requiring navigation.
            DispatcherQueue.TryEnqueue(() =>
            {
                var refreshed = App.Vault.Load(Task.Id);
                if (refreshed is not null) ApplyTask(refreshed);
            });
        }
    }

    private void AddSubtaskBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitNewSubtask();
            e.Handled = true;
        }
    }

    private void AddSubtask_Click(object sender, RoutedEventArgs e) => CommitNewSubtask();

    private void CommitNewSubtask()
    {
        if (_isLoading) return;
        var title = AddSubtaskBox.Text?.Trim();
        if (string.IsNullOrEmpty(title)) return;

        App.Vault.AddSubtask(Task.Id, title);
        AddSubtaskBox.Text = string.Empty;

        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null)
        {
            Task = reloaded;
            BindSubtasks(reloaded.Subtasks);
        }

    }

    private void ToggleSubtaskMyDay_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        if (sender is FrameworkElement fe && fe.DataContext is SubTask sub)
        {
            var newValue = !sub.IsMyDay;
            App.Vault.SetSubtaskMyDay(Task.Id, sub.Text, newValue);
            // Reload the task from disk so subsequent UI binding reflects the change.
            var reloaded = App.Vault.Load(Task.Id);
            if (reloaded is not null)
            {
                Task = reloaded;
                BindSubtasks(reloaded.Subtasks);
            }

        }
    }

    private async void SubtaskDue_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not SubTask sub) return;

        await PromptSetDueAsync(sub);
    }

    private async void DeleteSubtask_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not SubTask sub) return;

        var dialog = new ContentDialog
        {
            Title = "Delete subtask?",
            Content = $"\"{sub.Text}\" will be removed from this task. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var index = Task.Subtasks.IndexOf(sub);
        if (index < 0) return;

        try
        {
            App.Tasks.DeleteSubtask(Task, index);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DeleteSubtask failed: {ex}");
            return;
        }

        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null)
        {
            Task = reloaded;
            BindSubtasks(reloaded.Subtasks);
        }

    }

    private async void OpenObsidian_Click(object sender, RoutedEventArgs e)
        => await OpenCurrentTaskInObsidianAsync();

    private async void OpenInObsidian_Accelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await OpenCurrentTaskInObsidianAsync();
    }

    private async System.Threading.Tasks.Task OpenCurrentTaskInObsidianAsync()
    {
        var absolutePath = Path.Combine(App.Vault.VaultPath, $"{Task.Id}.md");
        var vaultRelative = ToVaultRelativePath(absolutePath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    private void CopyTaskLink_Click(object sender, RoutedEventArgs e)
    {
        var uri = Glasswork.Core.Models.GlassworkUriParser.Build(
            new Glasswork.Core.Models.GlassworkUri.Task(Task.Id));
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(uri);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        ClipboardHint.Text = $"Copied — {uri}";
    }

    private async void OpenArtifactInObsidian_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string artifactPath) return;
        var vaultRelative = ToVaultRelativePath(artifactPath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    private void ArtifactShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ArtifactRow row)
        {
            return;
        }

        var availability = ArtifactShareFormatter.GetAvailability(row.Artifact);
        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateArtifactShareItem(
            "Copy formatted",
            "ArtifactShareCopyFormatted",
            availability.CanCopyFormatted,
            async () => await ArtifactShareService.CopyToClipboardAsync(row, ArtifactShareClipboardFormat.Formatted)));
        flyout.Items.Add(CreateArtifactShareItem(
            "Copy Markdown",
            "ArtifactShareCopyMarkdown",
            availability.CanCopyMarkdown,
            async () => await ArtifactShareService.CopyToClipboardAsync(row, ArtifactShareClipboardFormat.Markdown)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateArtifactShareItem(
            "Save a copy...",
            "ArtifactShareSaveCopy",
            availability.CanSaveCopy,
            () => ArtifactShareService.SaveCopyAsync(row)));
        flyout.Items.Add(CreateArtifactShareItem(
            "Show in folder",
            "ArtifactShareShowInFolder",
            availability.CanShowInFolder,
            () => System.Threading.Tasks.Task.FromResult<string?>(ArtifactShareService.ShowInFolder(row) ?? "Opened artifact folder.")));

        flyout.ShowAt(button);
    }

    private MenuFlyoutItem CreateArtifactShareItem(
        string text,
        string automationId,
        bool isEnabled,
        Func<System.Threading.Tasks.Task<string?>> action)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = isEnabled,
        };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += async (_, _) =>
        {
            var message = await action();
            ShowArtifactShareStatus(message);
        };
        return item;
    }

    private void ShowArtifactShareStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ClipboardHint.Text = message;
    }

    private static string? ToVaultRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        // App.Vault.VaultPath is the todo folder (~/Wiki/wiki/todo). The Obsidian
        // vault root sits two levels above (~/Wiki) so that vault-relative paths
        // like "wiki/todo/TASK.md" resolve to the actual on-disk location and the
        // generated obsidian:// URI is an exact path match (not a basename fallback).
        var todoDir = App.Vault.VaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var vaultRoot = Path.GetDirectoryName(Path.GetDirectoryName(todoDir));
        if (string.IsNullOrWhiteSpace(vaultRoot)) return null;

        try
        {
            return Path.GetRelativePath(vaultRoot, absolutePath);
        }
        catch
        {
            return null;
        }
    }

    private void OnArtifactMarkdownLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not VaultMarkdownView view) return;
        view.WikiLinkResolver ??= VaultPageHelper.BuildWikiLinkResolver();
        view.LinkClicked -= OnArtifactLinkClicked;
        view.LinkClicked += OnArtifactLinkClicked;
    }

    private void OnArtifactMarkdownUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not VaultMarkdownView view) return;
        view.LinkClicked -= OnArtifactLinkClicked;
    }

    private async void OnArtifactLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        await VaultPageHelper.RouteLinkClickAsync(Frame, e);
    }

    private void OpenAdo_Click(object sender, RoutedEventArgs e)
    {
        if (!Task.AdoLink.HasValue) return;
        var baseUrl = (App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)) return;
        var url = $"{baseUrl}/_workitems/edit/{Task.AdoLink.Value}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void EditAdoLink_Click(object sender, RoutedEventArgs e)
    {
        if (Task.IsCancelled) return;
        var idBox = new TextBox
        {
            Header = "ADO work item ID (leave blank to clear)",
            PlaceholderText = "e.g. 12345",
            Text = Task.AdoLink?.ToString() ?? string.Empty,
        };
        var titleBox = new TextBox
        {
            Header = "ADO title (optional — auto-fetched if left blank)",
            PlaceholderText = "Short label shown on the task",
            Text = Task.AdoTitle ?? string.Empty,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var fetchStatus = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Opacity = 0.7,
        };
        var panel = new StackPanel { MinWidth = 360 };
        panel.Children.Add(idBox);
        panel.Children.Add(titleBox);
        panel.Children.Add(fetchStatus);

        var dialog = new ContentDialog
        {
            Title = "Edit ADO link",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);

        // Deferral pattern: when Save is clicked with an ID but no title, try to
        // fetch the title from ADO before persisting. Failures are silent (we just
        // save with whatever the user typed) so the dialog never gets stuck.
        dialog.PrimaryButtonClick += async (s, args) =>
        {
            var raw = idBox.Text?.Trim() ?? string.Empty;
            if (raw.Length == 0) return;
            if (!int.TryParse(raw, out var parsed) || parsed <= 0) return;
            if (!string.IsNullOrWhiteSpace(titleBox.Text)) return;

            var baseUrl = (App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty).Trim();
            if (baseUrl.Length == 0) return;

            var deferral = args.GetDeferral();
            idBox.IsEnabled = false;
            titleBox.IsEnabled = false;
            fetchStatus.Text = $"Fetching title for #{parsed}…";
            fetchStatus.Visibility = Visibility.Visible;
            try
            {
                var fetched = await App.AdoFetcher.TryFetchTitleAsync(parsed, baseUrl);
                if (!string.IsNullOrEmpty(fetched))
                {
                    titleBox.Text = fetched;
                }
            }
            catch { /* never block save */ }
            finally
            {
                idBox.IsEnabled = true;
                titleBox.IsEnabled = true;
                fetchStatus.Visibility = Visibility.Collapsed;
                deferral.Complete();
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var rawFinal = idBox.Text?.Trim() ?? string.Empty;
        int? newId = null;
        if (rawFinal.Length > 0)
        {
            if (!int.TryParse(rawFinal, out var parsed) || parsed <= 0) return;
            newId = parsed;
        }
        var newTitle = string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text.Trim();

        App.Vault.SetAdoLink(Task.Id, newId, newTitle);
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    private void OpenParent_Click(object sender, RoutedEventArgs e)
    {
        if (_parentResolution is null) return;

        switch (_parentResolution.Type)
        {
            case ParentLinkResolution.ResolutionType.InAppTask:
                // Navigate to parent task
                var parentTask = App.Index.ById(_parentResolution.TaskId ?? string.Empty);
                if (parentTask is not null)
                    Frame.Navigate(typeof(TaskDetailPage), parentTask);
                break;

            case ParentLinkResolution.ResolutionType.AdoUrl:
                // Open in external browser
                if (_parentResolution.Url is not null)
                    Process.Start(new ProcessStartInfo(_parentResolution.Url) { UseShellExecute = true });
                break;

            case ParentLinkResolution.ResolutionType.None:
                // No action
                break;
        }
    }

    private async void EditParent_Click(object sender, RoutedEventArgs e)
    {
        if (Task.IsCancelled) return;
        var box = new TextBox
        {
            Header = "Parent (ADO ID, full URL, or free text — leave blank to clear)",
            PlaceholderText = "e.g. 12345  or  https://dev.azure.com/org/proj/_workitems/edit/12345",
            Text = Task.Parent ?? string.Empty,
            MinWidth = 420,
        };

        var dialog = new ContentDialog
        {
            Title = "Edit parent",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var trimmed = box.Text?.Trim();
        App.Vault.SetParent(Task.Id, string.IsNullOrEmpty(trimmed) ? null : trimmed);
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    private void StartWork_Click(object sender, RoutedEventArgs e)
        => CopyInvocation(TaskInvocationFormatter.FormatStartWork(Task.Id));

    private void Resume_Click(object sender, RoutedEventArgs e)
        => CopyInvocation(TaskInvocationFormatter.FormatResume(Task.Id));

    private void WrapUp_Click(object sender, RoutedEventArgs e)
        => CopyInvocation(TaskInvocationFormatter.FormatWrapUp(Task.Id));

    private void CopyInvocation(string line)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(line);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        ClipboardHint.Text = "Copied — paste into your Copilot CLI session.";
    }

    private async Task<bool> SaveAsync(bool overwrite = false)
    {
        if (Task.IsCancelled)
        {
            await ShowOperationErrorAsync(
                "Unable to save Task",
                "Restore the Cancelled Task before editing it.");
            return false;
        }

        try
        {
            var result = overwrite
                ? _saveController.Overwrite(Task)
                : _saveController.Save(Task);

            switch (result)
            {
                case TaskEditSaveResult.Saved:
                    return true;
                case TaskEditSaveResult.Conflict:
                    ReloadBanner.IsOpen = true;
                    return false;
                case TaskEditSaveResult.Missing:
                    await ShowOperationErrorAsync(
                        "Unable to save task",
                        "The task file no longer exists in the active task folder.");
                    return false;
                case TaskEditSaveResult.ReadOnly:
                    await ShowOperationErrorAsync(
                        "Unable to save Task",
                        "Restore the Cancelled Task before editing it.");
                    return false;
                default:
                    throw new InvalidOperationException($"Unknown task save result: {result}");
            }
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("Unable to save task", ex.Message);
            return false;
        }
    }

    private void RestoreStatusSelection()
    {
        _isLoading = true;
        SetComboByTag(StatusBox, Task.Status);
        _isLoading = false;
    }

    private void ReloadTaskFromVault()
    {
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);
    }

    private async Task<string?> PromptBlockedReasonAsync(string title, string? initialReason)
    {
        var box = new TextBox
        {
            Text = initialReason ?? string.Empty,
            PlaceholderText = "Why is this task blocked?",
            MinWidth = 360
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        var trimmed = box.Text?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private async Task<string?> PromptResumeStatusAsync(string? initialStatus)
    {
        var statusBox = new ComboBox { MinWidth = 240 };
        statusBox.Items.Add(new ComboBoxItem { Tag = GlassworkTask.Statuses.Todo, Content = "To Do" });
        statusBox.Items.Add(new ComboBoxItem { Tag = GlassworkTask.Statuses.InProgress, Content = "In Progress" });
        SetComboByTag(statusBox, initialStatus is GlassworkTask.Statuses.InProgress ? GlassworkTask.Statuses.InProgress : GlassworkTask.Statuses.Todo);

        var dialog = new ContentDialog
        {
            Title = "Resume blocked task",
            Content = statusBox,
            PrimaryButtonText = "Resume",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;
        return (statusBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    }

    private async Task<(string? Reason, string? FromStatus)> PromptBlockedRepairAsync()
    {
        var reasonBox = new TextBox
        {
            Text = Task.BlockedReason ?? string.Empty,
            PlaceholderText = "Why is this task blocked?",
            MinWidth = 360
        };
        var statusBox = new ComboBox { MinWidth = 240 };
        statusBox.Items.Add(new ComboBoxItem { Tag = GlassworkTask.Statuses.Todo, Content = "To Do" });
        statusBox.Items.Add(new ComboBoxItem { Tag = GlassworkTask.Statuses.InProgress, Content = "In Progress" });
        SetComboByTag(statusBox, Task.BlockedFromStatus is GlassworkTask.Statuses.InProgress ? GlassworkTask.Statuses.InProgress : GlassworkTask.Statuses.Todo);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(reasonBox);
        panel.Children.Add(statusBox);

        var dialog = new ContentDialog
        {
            Title = "Repair blocked task",
            Content = panel,
            PrimaryButtonText = "Repair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return (null, null);
        return (reasonBox.Text?.Trim(), (statusBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
    }

    private async Task ShowOperationErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);
        await dialog.ShowAsync();
    }

    private async void BlockTask_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var reason = await PromptBlockedReasonAsync("Mark blocked", null);
            if (reason is null) return;
            App.Tasks.MarkBlocked(Task, reason);
            ReloadTaskFromVault();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("Unable to mark task blocked", ex.Message);
        }
    }

    private async void EditBlocker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var reason = await PromptBlockedReasonAsync("Edit blocker", Task.BlockedReason);
            if (reason is null) return;
            App.Tasks.EditBlockedReason(Task, reason);
            ReloadTaskFromVault();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("Unable to edit blocker", ex.Message);
        }
    }

    private async void RepairBlocked_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var repair = await PromptBlockedRepairAsync();
            if (string.IsNullOrWhiteSpace(repair.Reason) || string.IsNullOrWhiteSpace(repair.FromStatus)) return;
            App.Tasks.RepairBlocked(Task, repair.Reason, repair.FromStatus);
            ReloadTaskFromVault();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("Unable to repair blocked task", ex.Message);
        }
    }

    private async void ResumeBlocked_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await PromptResumeStatusAsync(Task.BlockedFromStatus);
            if (string.IsNullOrWhiteSpace(status)) return;
            App.Tasks.ResumeBlocked(Task, status);
            ReloadTaskFromVault();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("Unable to resume blocked task", ex.Message);
        }
    }

    private async void MarkBlockedDone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Tasks.SetStatus(Task, GlassworkTask.Statuses.Done);
            ReloadTaskFromVault();
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("Unable to complete blocked task", ex.Message);
        }
    }

    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        var reasonBox = new TextBox
        {
            Header = "Reason (optional)",
            PlaceholderText = "Why are you cancelling this task?",
            MinWidth = 360,
        };
        AutomationProperties.SetAutomationId(reasonBox, "CancelTaskReasonBox");

        var dialog = new ContentDialog
        {
            Title = "Cancel task?",
            Content = reasonBox,
            PrimaryButtonText = "Cancel task",
            CloseButtonText = "Keep task",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        AutomationProperties.SetAutomationId(dialog, "CancelTaskDialog");
        dialog.WithAppTheme(this);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var reason = string.IsNullOrWhiteSpace(reasonBox.Text)
                ? "Cancelled by user"
                : reasonBox.Text.Trim();
            var taskToCancel = Task.Clone();
            App.Tasks.Cancel(taskToCancel, reason);
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(BacklogPage));
        }
        catch (Exception ex)
        {
            await ShowOperationErrorAsync("Unable to cancel task", ex.Message);
        }
    }

    private async void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (_isDeletingTask) return;
        _isDeletingTask = true;
        DeleteTaskButton.IsEnabled = false;
        var taskId = Task.Id;
        var mutations = App.Mutations;
        var vaultPath = App.Vault.VaultPath;
        try
        {
            var preflightResult = await System.Threading.Tasks.Task.Run(
                () => mutations.PreflightTaskDeletion(taskId));
            if (!_isNavigated
                || XamlRoot is null
                || !ReferenceEquals(mutations, App.Mutations)
                || !string.Equals(vaultPath, App.Vault.VaultPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Task.Id, taskId, StringComparison.Ordinal))
            {
                return;
            }
            if (preflightResult.Outcome != "ready" || preflightResult.Preflight is null)
            {
                await ShowOperationErrorAsync(
                    "Unable to prepare Task deletion",
                    preflightResult.Error ?? "The Task deletion preflight failed.");
                return;
            }

            var viewModel = new TaskDeletionDialogViewModel(preflightResult.Preflight);
            var impact = new TextBlock
            {
                Text = viewModel.ImpactSummary,
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetAutomationId(impact, "DeleteTaskImpactSummary");

            var content = new StackPanel { Spacing = 12, MinWidth = 420 };
            content.Children.Add(new TextBlock
            {
                Text = "This cannot be undone. Inbound wiki links will be replaced without changing surrounding prose.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(impact);

            CheckBox? cascadeCheckBox = null;
            if (viewModel.RequiresCascade)
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"Descendants: {viewModel.DescendantIds}",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75,
                });
                cascadeCheckBox = new CheckBox
                {
                    Content = $"I understand: also permanently delete all "
                        + $"{viewModel.Preflight.Descendants.Count} "
                        + (viewModel.Preflight.Descendants.Count == 1
                            ? "descendant"
                            : "descendants"),
                    IsChecked = false,
                };
                AutomationProperties.SetAutomationId(cascadeCheckBox, "DeleteCascadeCheckBox");
                content.Children.Add(cascadeCheckBox);
            }

            var confirmationBox = new TextBox
            {
                Header = $"Type \"{viewModel.Preflight.Task.Title}\" to confirm",
                MinWidth = 400,
            };
            AutomationProperties.SetAutomationId(
                confirmationBox,
                "DeleteTaskConfirmTitleBox");
            content.Children.Add(confirmationBox);

            var dialog = new ContentDialog
            {
                Title = "Permanently delete Task?",
                Content = content,
                PrimaryButtonText = "Delete permanently",
                CloseButtonText = "Keep Task",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
                XamlRoot = XamlRoot,
            };
            AutomationProperties.SetAutomationId(dialog, "DeleteTaskDialog");
            dialog.WithAppTheme(this);

            void UpdateDeleteEnabled()
            {
                viewModel.ConfirmationTitle = confirmationBox.Text ?? string.Empty;
                viewModel.CascadeChildren = cascadeCheckBox?.IsChecked == true;
                dialog.IsPrimaryButtonEnabled = viewModel.CanDelete;
            }

            confirmationBox.TextChanged += (_, _) => UpdateDeleteEnabled();
            if (cascadeCheckBox is not null)
            {
                cascadeCheckBox.Checked += (_, _) => UpdateDeleteEnabled();
                cascadeCheckBox.Unchecked += (_, _) => UpdateDeleteEnabled();
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            if (!_isNavigated
                || XamlRoot is null
                || !ReferenceEquals(mutations, App.Mutations)
                || !string.Equals(vaultPath, App.Vault.VaultPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Task.Id, taskId, StringComparison.Ordinal))
            {
                return;
            }

            var outcome = await System.Threading.Tasks.Task.Run(() =>
                mutations.DeleteTask(
                    $"app-delete-{Guid.NewGuid():N}",
                    viewModel.Preflight.Task.Id,
                    viewModel.Preflight.Task.ResourceRevision,
                    viewModel.ConfirmationTitle,
                    viewModel.CascadeChildren,
                    viewModel.Preflight.PreflightRevision));
            if (!_isNavigated || XamlRoot is null)
                return;
            if (outcome.Outcome != "applied")
            {
                await ShowOperationErrorAsync(
                    "Unable to delete Task",
                    outcome.Error ?? $"Task deletion failed: {outcome.Outcome}.");
                return;
            }

            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(BacklogPage));
        }
        catch (Exception ex)
        {
            if (_isNavigated && XamlRoot is not null)
            {
                try
                {
                    await ShowOperationErrorAsync("Unable to delete Task", ex.Message);
                }
                catch (Exception dialogException)
                {
                    Debug.WriteLine($"Unable to show Task deletion error: {dialogException}");
                }
            }
        }
        finally
        {
            _isDeletingTask = false;
            if (_isNavigated)
                DeleteTaskButton.IsEnabled = true;
        }
    }

    // ============================================================
    // Subtask "..." menu, detail dialog, drag-reorder
    // ============================================================

    private void SubtaskMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SubTask sub) return;

        var menu = new MenuFlyout();

        // Set status submenu
        var statusItem = new MenuFlyoutSubItem { Text = "Set status" };
        AddStatusOption(statusItem, sub, "todo", "To Do");
        AddStatusOption(statusItem, sub, "in_progress", "In Progress");
        AddStatusOption(statusItem, sub, "blocked", "Blocked");
        AddStatusOption(statusItem, sub, "done", "Done");
        AddStatusOption(statusItem, sub, "dropped", "Dropped");
        menu.Items.Add(statusItem);

        // Set due...
        var dueItem = new MenuFlyoutItem { Text = "Set due date..." };
        dueItem.Click += async (_, __) => await PromptSetDueAsync(sub);
        menu.Items.Add(dueItem);

        // Clear due date (only shown when dated)
        if (sub.Due.HasValue)
        {
            var clearDueItem = new MenuFlyoutItem { Text = "Clear due date" };
            clearDueItem.Click += (_, __) =>
            {
                App.Vault.SetSubtaskDue(Task.Id, sub.Text, null);
                var reloaded = App.Vault.Load(Task.Id);
                if (reloaded is not null) ApplyTask(reloaded);

            };
            menu.Items.Add(clearDueItem);
        }

        // Edit text...
        var textItem = new MenuFlyoutItem { Text = "Edit text..." };
        textItem.Click += async (_, __) => await PromptEditTextAsync(sub);
        menu.Items.Add(textItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Open detail
        var detailItem = new MenuFlyoutItem { Text = "Open detail..." };
        detailItem.Click += async (_, __) => await OpenSubtaskDetailAsync(sub);
        menu.Items.Add(detailItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = "Delete" };
        deleteItem.Click += (_, __) => DeleteSubtask_Click(fe, new RoutedEventArgs());
        menu.Items.Add(deleteItem);

        menu.ShowAt(fe);
    }

    // Completed row: reduced action set per ADR 0004.
    // No "Set due" (done items don't get reschedule), no "My Day" toggle.
    // "Set status" submenu only offers re-opening states (in_progress / blocked / dropped).
    private void CompletedSubtaskMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SubTask sub) return;

        var menu = new MenuFlyout();

        var statusItem = new MenuFlyoutSubItem { Text = "Set status" };
        AddStatusOption(statusItem, sub, "in_progress", "In Progress");
        AddStatusOption(statusItem, sub, "blocked", "Blocked");
        AddStatusOption(statusItem, sub, "dropped", "Dropped");
        menu.Items.Add(statusItem);

        var textItem = new MenuFlyoutItem { Text = "Edit text..." };
        textItem.Click += async (_, __) => await PromptEditTextAsync(sub);
        menu.Items.Add(textItem);

        var promoteItem = new MenuFlyoutItem { Text = "Promote to top-level task" };
        promoteItem.Click += (_, __) => PromoteSubtask(sub);
        menu.Items.Add(promoteItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var detailItem = new MenuFlyoutItem { Text = "Open detail..." };
        detailItem.Click += async (_, __) => await OpenSubtaskDetailAsync(sub);
        menu.Items.Add(detailItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = "Delete" };
        deleteItem.Click += (_, __) => DeleteSubtask_Click(fe, new RoutedEventArgs());
        menu.Items.Add(deleteItem);

        menu.ShowAt(fe);
    }

    private void PromoteSubtask(SubTask sub)
    {
        var index = Task.Subtasks.IndexOf(sub);
        if (index < 0) return;
        try
        {
            var promoted = App.Tasks.PromoteSubtask(Task, index);
            var refreshed = App.Vault.Load(Task.Id);
            if (refreshed is not null) ApplyTask(refreshed);

            if (promoted is not null) Frame.Navigate(typeof(TaskDetailPage), promoted);
        }
        catch (Exception ex) { Debug.WriteLine($"PromoteSubtask failed: {ex}"); }
    }

    private void AddStatusOption(MenuFlyoutSubItem parent, SubTask sub, string status, string label)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += (_, __) => ApplyStatusChange(sub, status);
        parent.Items.Add(item);
    }

    private void ApplyStatusChange(SubTask sub, string newStatus)
    {
        var index = Task.Subtasks.IndexOf(sub);
        if (index < 0) return;

        var fresh = App.Vault.Load(Task.Id);
        if (fresh is null || index >= fresh.Subtasks.Count) return;
        var target = fresh.Subtasks[index];

        // Status `todo` is represented as "no status field"; everything else writes a status.
        target.Status = newStatus == "todo" ? null : newStatus;
        // Sync checkbox char with effective doneness.
        target.IsCompleted = newStatus is "done" or "dropped";
        // Status leaves blocked → clear blocker reason.
        if (newStatus != "blocked")
            target.Metadata.Remove("blocker");

        App.Vault.Save(fresh);
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    private async System.Threading.Tasks.Task PromptSetDueAsync(SubTask sub)
    {
        var picker = new CalendarDatePicker
        {
            Header = "Due date (clear to remove)",
            Date = sub.Due.HasValue ? new DateTimeOffset(sub.Due.Value) : (DateTimeOffset?)null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = "Set due date",
            Content = picker,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var index = Task.Subtasks.IndexOf(sub);
        if (index < 0) return;
        var fresh = App.Vault.Load(Task.Id);
        if (fresh is null || index >= fresh.Subtasks.Count) return;
        fresh.Subtasks[index].Due = picker.Date?.DateTime;
        App.Vault.Save(fresh);
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    private async System.Threading.Tasks.Task PromptEditTextAsync(SubTask sub)
    {
        var box = new TextBox { Text = sub.Text, MinWidth = 360 };
        var dialog = new ContentDialog
        {
            Title = "Edit subtask text",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        dialog.WithAppTheme(this);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var newText = (box.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newText)) return;

        var index = Task.Subtasks.IndexOf(sub);
        if (index < 0) return;
        var fresh = App.Vault.Load(Task.Id);
        if (fresh is null || index >= fresh.Subtasks.Count) return;
        fresh.Subtasks[index].Text = newText;
        App.Vault.Save(fresh);
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    private async void SubtaskText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SubTask sub)
            await OpenSubtaskDetailAsync(sub);
    }

    private async System.Threading.Tasks.Task OpenSubtaskDetailAsync(SubTask sub)
    {
        var dialog = new SubtaskDetailDialog(sub) { XamlRoot = this.XamlRoot };
        dialog.WithAppTheme(this);
        var result = await dialog.ShowAsync();

        var index = Task.Subtasks.IndexOf(sub);
        if (index < 0) return;

        if (dialog.Delete)
        {
            try { App.Tasks.DeleteSubtask(Task, index); }
            catch (Exception ex) { Debug.WriteLine($"DeleteSubtask failed: {ex}"); return; }
            var afterDel = App.Vault.Load(Task.Id);
            if (afterDel is not null) ApplyTask(afterDel);

            return;
        }

        if (dialog.Promote)
        {
            try
            {
                var promoted = App.Tasks.PromoteSubtask(Task, index);
                var refreshed = App.Vault.Load(Task.Id);
                if (refreshed is not null) ApplyTask(refreshed);

                if (promoted is not null) Frame.Navigate(typeof(TaskDetailPage), promoted);
            }
            catch (Exception ex) { Debug.WriteLine($"PromoteSubtask failed: {ex}"); }
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        // Apply edits via reload-mutate-Save.
        var fresh = App.Vault.Load(Task.Id);
        if (fresh is null || index >= fresh.Subtasks.Count) return;
        var target = fresh.Subtasks[index];
        var v = dialog.Result;

        target.Text = v.Text;
        target.Status = v.Status;
        target.IsCompleted = v.IsCompleted;
        target.Notes = v.Notes;
        target.Due = v.Due;

        if (v.AdoId.HasValue) target.Metadata["ado"] = v.AdoId.Value.ToString();
        else target.Metadata.Remove("ado");

        if (v.Status == "blocked" && !string.IsNullOrWhiteSpace(v.BlockerReason))
            target.Metadata["blocker"] = v.BlockerReason!;
        else
            target.Metadata.Remove("blocker");

        if (v.IsMyDay)
            target.Metadata["my_day"] = DateTime.Today.ToString("yyyy-MM-dd");
        else
            target.Metadata.Remove("my_day");

        App.Vault.Save(fresh);
        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    private void ActiveSubtaskList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_isLoading) return;
        if (sender.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<SubTask> active) return;

        // Rebuild Task.Subtasks as: active (in new order) + completed (preserving original relative order).
        var completed = Task.Subtasks.Where(s => s.IsEffectivelyDone).ToList();
        var newOrder = new List<SubTask>(active.Count + completed.Count);
        newOrder.AddRange(active);
        newOrder.AddRange(completed);

        // Persist via repeated ReorderSubtask calls would be O(n^2); instead just save the whole task.
        var fresh = App.Vault.Load(Task.Id);
        if (fresh is null) return;
        // Map the in-memory active order to indices in `fresh.Subtasks` by Text + Status (best-effort
        // identity match — since duplicate titles are possible we walk and consume matches in order).
        var freshActive = fresh.Subtasks.Where(s => !s.IsEffectivelyDone).ToList();
        var freshCompleted = fresh.Subtasks.Where(s => s.IsEffectivelyDone).ToList();
        if (freshActive.Count != active.Count)
        {
            // Disk diverged from UI between bind and drop; reload and abort the reorder.
            ApplyTask(fresh);
            return;
        }

        // Build a permutation of freshActive matching the new order. Walk active (UI order) and pop the
        // first matching freshActive entry by reference-equivalent fields.
        var pool = new List<SubTask>(freshActive);
        var reorderedActive = new List<SubTask>(active.Count);
        foreach (var ui in active)
        {
            var match = pool.FirstOrDefault(p => p.Text == ui.Text && p.Status == ui.Status);
            if (match is null) { ApplyTask(fresh); return; }
            pool.Remove(match);
            reorderedActive.Add(match);
        }

        fresh.Subtasks.Clear();
        foreach (var s in reorderedActive) fresh.Subtasks.Add(s);
        foreach (var s in freshCompleted) fresh.Subtasks.Add(s);
        App.Vault.Save(fresh);

        var reloaded = App.Vault.Load(Task.Id);
        if (reloaded is not null) ApplyTask(reloaded);

    }

    private static void SetComboByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }
}

/// <summary>
/// Bool → Visibility converter (true = Visible, false = Collapsed).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>
/// Hex string ("#RRGGBB") → SolidColorBrush converter for status pills.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && hex.StartsWith('#') && hex.Length == 7)
        {
            byte r = System.Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = System.Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = System.Convert.ToByte(hex.Substring(5, 2), 16);
            return new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
