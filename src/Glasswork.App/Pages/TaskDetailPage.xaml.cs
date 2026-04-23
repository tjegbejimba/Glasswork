using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Glasswork.Pages;

public sealed partial class TaskDetailPage : Page
{
    public GlassworkTask Task { get; private set; } = new();

    private bool _isLoading;

    public TaskDetailPage()
    {
        InitializeComponent();
        // Always re-create this page on navigation so Reload (which re-navigates
        // with a fresh GlassworkTask) cannot be deduped to the cached instance.
        NavigationCacheMode = NavigationCacheMode.Disabled;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.ObsidianLauncher.NotInstalled += OnObsidianNotInstalled;
        if (e.Parameter is TaskDetailNavigation nav)
        {
            // Navigated from My Day's "flagged subtasks" section — display the parent task
            // (FocusSubtaskTitle is currently informational; UI affordance for scrolling could
            // be added later).
            App.TaskFileChangedExternally += OnFileChangedExternally;
            App.ArtifactChangedExternally += OnArtifactChangedExternally;
            App.BacklinksChangedExternally += OnBacklinksChangedExternally;
            ApplyTask(nav.Task);
            return;
        }
        if (e.Parameter is GlassworkTask task)
        {
            App.TaskFileChangedExternally += OnFileChangedExternally;
            App.ArtifactChangedExternally += OnArtifactChangedExternally;
            App.BacklinksChangedExternally += OnBacklinksChangedExternally;
            ApplyTask(task);
        }
    }

    private void ApplyTask(GlassworkTask task)
    {
        _isLoading = true;
        Task = task;
        App.ActiveTask.ActiveTaskId = task.Id;

        // Set combo boxes to match task state
        SetComboByTag(StatusBox, task.Status);
        SetComboByTag(PriorityBox, task.Priority);

        DueDatePicker.Date = task.Due.HasValue
            ? new DateTimeOffset(task.Due.Value)
            : (DateTimeOffset?)null;

        BindSubtasks(task.Subtasks);
        BindRelated(task.RelatedLinks);
        BindArtifacts(task.Id);
        BindBacklinks(task.Id);

        CreatedText.Text = $"Created: {task.Created:yyyy-MM-dd}";
        CompletedText.Text = task.Status == GlassworkTask.Statuses.Done && task.CompletedAt.HasValue
            ? $"Completed: {task.CompletedAt.Value:yyyy-MM-dd HH:mm}"
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

        _isLoading = false;
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
            return;
        }

        ParentLabel.Visibility = Visibility.Visible;
        ParentTextRun.Text = p;
        EditParentButton.Content = "Edit parent";

        var baseUrl = (App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty).Trim();
        var url = AdoLinkResolver.TryResolve(p, baseUrl);
        ParentLinkButton.Visibility = url is null ? Visibility.Collapsed : Visibility.Visible;
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

        ArtifactsSection.Visibility = Visibility.Visible;
        ArtifactsList.ItemsSource = ArtifactRow.Project(artifacts, DateTime.UtcNow);
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
        await App.ObsidianLauncher.Open($"wiki/{link.Slug}");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.TaskFileChangedExternally -= OnFileChangedExternally;
        App.ArtifactChangedExternally -= OnArtifactChangedExternally;
        App.BacklinksChangedExternally -= OnBacklinksChangedExternally;
        App.ObsidianLauncher.NotInstalled -= OnObsidianNotInstalled;
        App.ActiveTask.Clear();
    }

    private void OnObsidianNotInstalled(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ObsidianInstallBanner.IsOpen = true);
    }

    private void OnFileChangedExternally(object? sender, string fileName)
    {
        if (!App.ActiveTask.IsActive(fileName)) return;
        // Watcher fires on a thread-pool thread; show the banner on the UI thread.
        DispatcherQueue.TryEnqueue(() => ReloadBanner.IsOpen = true);
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

    private void KeepMine_Click(object sender, RoutedEventArgs e)
    {
        // Dismiss only — the next Save() will overwrite the on-disk change.
        ReloadBanner.IsOpen = false;
    }

    private void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isLoading) Save();
    }

    private void Status_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (StatusBox.SelectedItem is ComboBoxItem item)
        {
            var status = item.Tag?.ToString() ?? "todo";
            App.Tasks.SetStatus(Task, status);
        }
    }

    private void Priority_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (PriorityBox.SelectedItem is ComboBoxItem item)
        {
            Task.Priority = item.Tag?.ToString() ?? "medium";
            Save();
        }
    }

    private void DueDate_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_isLoading) return;
        Task.Due = args.NewDate?.DateTime;
        Save();
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
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
            try { App.Index.Refresh(); } catch { /* best-effort */ }
        }
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        App.Vault.Delete(Task.Id);
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void OpenObsidian_Click(object sender, RoutedEventArgs e)
    {
        await App.ObsidianLauncher.Open($"wiki/todo/{Task.Id}");
    }

    private async void OpenArtifactInObsidian_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string artifactPath) return;
        var vaultRelative = ToVaultRelativePath(artifactPath);
        if (vaultRelative is null) return;
        await App.ObsidianLauncher.Open(vaultRelative);
    }

    private static string? ToVaultRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;

        var todoDir = App.Vault.VaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var wikiRoot = Path.GetDirectoryName(todoDir);
        var vaultRoot = wikiRoot is null ? null : Path.GetDirectoryName(wikiRoot);
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
    }

    private void OpenParent_Click(object sender, RoutedEventArgs e)
    {
        var p = Task.Parent?.Trim();
        if (string.IsNullOrEmpty(p)) return;
        var baseUrl = (App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty).Trim();
        var url = AdoLinkResolver.TryResolve(p, baseUrl);
        if (url is null) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void EditParent_Click(object sender, RoutedEventArgs e)
    {
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
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

    private void Save() => App.Vault.Save(Task);

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
            try { App.Index.Refresh(); } catch { /* best-effort */ }
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
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
            try { App.Index.Refresh(); } catch { /* best-effort */ }
            return;
        }

        if (dialog.Promote)
        {
            try
            {
                var promoted = App.Tasks.PromoteSubtask(Task, index);
                var refreshed = App.Vault.Load(Task.Id);
                if (refreshed is not null) ApplyTask(refreshed);
                try { App.Index.Refresh(); } catch { /* best-effort */ }
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
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
        try { App.Index.Refresh(); } catch { /* best-effort */ }
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

/// <summary>
/// Bool → SolidColorBrush converter that highlights the My Day toggle when active.
/// True returns the system accent brush; false returns a muted gray to indicate the
/// toggle is available but inactive.
/// </summary>
public sealed class MyDayBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b && b)
        {
            // Active: prefer system accent if available, fall back to a fixed accent color.
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color c)
                return new SolidColorBrush(c);
            return new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
        }
        return new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
