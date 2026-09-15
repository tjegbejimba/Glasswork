using System;
using System.Collections.ObjectModel;
using System.Linq;
using Glasswork.Controls;
using Glasswork.Core.Research;
using Glasswork.Core.Services;
using Glasswork.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Glasswork.Pages;

public sealed partial class WorkLogPage : Page
{
    private readonly WorkLogService _workLog;
    private DateTime _currentWeekStart;
    private string _currentLog = "";
    private bool _isInitialized;
    private WikiPageDocument? _agendaPage;

    public ObservableCollection<CancelledTaskRow> CancelledTasks { get; } = [];

    public WorkLogPage()
    {
        _workLog = new WorkLogService(App.Vault, App.TaskQuery);
        InitializeComponent();
        AgendaMarkdown.WikiLinkResolver = VaultPageHelper.BuildWikiLinkResolver();
        var agendaEnabled =
            App.UiState.Get<bool?>(App.WorkLogAgendaEnabledKey) ?? false;
        AgendaTab.Visibility = agendaEnabled ? Visibility.Visible : Visibility.Collapsed;
        var selectedTab = App.UiState.Get<string>(App.WorkLogSelectedTabKey);
        WorkLogTabs.SelectedItem = selectedTab switch
        {
            "agenda" when agendaEnabled => AgendaTab,
            "cancelled" => CancelledTab,
            _ => CompletedTab,
        };
        _isInitialized = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _currentWeekStart = GetMondayOfWeek(DateTime.Today);
        App.Index.TasksChanged += OnTasksChanged;
        App.Research.WikiPagesChanged += OnWikiPagesChanged;
        App.SupplementalInitializationChanged += OnSupplementalInitializationChanged;
        RefreshSelectedTab();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Index.TasksChanged -= OnTasksChanged;
        App.Research.WikiPagesChanged -= OnWikiPagesChanged;
        App.SupplementalInitializationChanged -= OnSupplementalInitializationChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnTasksChanged(object? sender, TasksChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshSelectedTab);
    }

    private void OnWikiPagesChanged(
        object? sender,
        WikiPagesChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var pageId = App.UiState.Get<string>(App.WorkLogAgendaPageIdKey);
            if (ReferenceEquals(WorkLogTabs.SelectedItem, AgendaTab)
                && pageId is not null
                && e.AffectedPageIds.Contains(pageId, StringComparer.OrdinalIgnoreCase))
            {
                RefreshAgenda(preserveScroll: true);
            }
        });
    }

    private void OnSupplementalInitializationChanged(
        object? sender,
        SupplementalInitializationChangedEventArgs e)
    {
        if (e.Component == SupplementalComponent.Research
            && e.Current.Status == SupplementalInitializationStatus.Ready
            && ReferenceEquals(WorkLogTabs.SelectedItem, AgendaTab))
        {
            DispatcherQueue.TryEnqueue(() => RefreshAgenda(preserveScroll: true));
        }
    }

    private void WorkLogTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;

        App.UiState.Set(App.WorkLogSelectedTabKey, SelectedTabKey());
        RefreshSelectedTab();
    }

    private void RefreshSelectedTab()
    {
        if (ReferenceEquals(WorkLogTabs.SelectedItem, AgendaTab))
            RefreshAgenda(preserveScroll: false);
        else if (ReferenceEquals(WorkLogTabs.SelectedItem, CancelledTab))
            RefreshCancelledTasks();
        else
            RefreshLog();
    }

    private string SelectedTabKey() =>
        ReferenceEquals(WorkLogTabs.SelectedItem, AgendaTab)
            ? "agenda"
            : ReferenceEquals(WorkLogTabs.SelectedItem, CancelledTab)
                ? "cancelled"
                : "completed";

    private void RefreshAgenda(bool preserveScroll)
    {
        var verticalOffset = preserveScroll ? AgendaScrollViewer.VerticalOffset : 0;
        var pageId = App.UiState.Get<string>(App.WorkLogAgendaPageIdKey);
        if (string.IsNullOrWhiteSpace(pageId))
        {
            _agendaPage = null;
            AgendaConfiguredView.Visibility = Visibility.Collapsed;
            AgendaUnavailableState.Visibility = Visibility.Collapsed;
            AgendaEmptyState.Visibility = Visibility.Visible;
            return;
        }

        var result = App.Research.ReadWikiPage(pageId);
        if (!result.Succeeded || result.Page is null)
        {
            _agendaPage = null;
            AgendaConfiguredView.Visibility = Visibility.Collapsed;
            AgendaEmptyState.Visibility = Visibility.Collapsed;
            AgendaUnavailableInfo.Message = result.Message;
            AgendaUnavailableState.Visibility = Visibility.Visible;
            return;
        }

        _agendaPage = result.Page;
        AgendaPageTitle.Text = result.Page.Title;
        AgendaUpdatedText.Text = result.Page.Updated is { } updated
            ? $"Updated {updated:MMM d, yyyy}"
            : "Updated date unavailable";
        AgendaMarkdown.Markdown = result.Page.Markdown;
        AgendaEmptyState.Visibility = Visibility.Collapsed;
        AgendaUnavailableState.Visibility = Visibility.Collapsed;
        AgendaConfiguredView.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() =>
            AgendaScrollViewer.ChangeView(
                horizontalOffset: null,
                verticalOffset,
                zoomFactor: null,
                disableAnimation: true));
    }

    private async void AgendaChoosePageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AgendaWikiPageDialog(App.Research)
        {
            XamlRoot = XamlRoot,
        };
        dialog.WithAppTheme(this);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || dialog.SelectedPage is null)
        {
            return;
        }

        App.UiState.Set(App.WorkLogAgendaPageIdKey, dialog.SelectedPage.Id);
        RefreshAgenda(preserveScroll: false);
    }

    private async void AgendaOpenInObsidianButton_Click(object sender, RoutedEventArgs e)
    {
        if (_agendaPage is not null)
            await App.ObsidianLauncher.Open(_agendaPage.VaultRelativePath);
    }

    private async void AgendaMarkdown_LinkClicked(
        object? sender,
        LinkClickedEventArgs e)
    {
        await VaultPageHelper.RouteLinkClickAsync(Frame, e);
    }

    private void RefreshLog()
    {
        _currentLog = _workLog.GenerateWeeklyLog(_currentWeekStart);
        LogContent.Text = _currentLog;
        WeekLabel.Text = $"Week of {_currentWeekStart:MMM d, yyyy}";
        var isEmpty = string.IsNullOrWhiteSpace(_currentLog);
        LogContent.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        CompletedEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshCancelledTasks()
    {
        CancelledTasks.Clear();
        foreach (var task in _workLog.GetCancelledTasks())
            CancelledTasks.Add(CancelledTaskRow.From(task));

        var isEmpty = CancelledTasks.Count == 0;
        CancelledTaskList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        CancelledEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RestoreCancelledTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CancelledTaskRow row }) return;

        try
        {
            var task = App.Vault.Load(row.Id)
                ?? throw new InvalidOperationException("The cancelled task no longer exists.");
            App.Tasks.RestoreCancelled(task);
            RefreshCancelledTasks();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Unable to restore task",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            dialog.WithAppTheme(this);
            await dialog.ShowAsync();
        }
    }

    private void OpenCancelledTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CancelledTaskRow row }) return;
        var task = App.Index.ById(row.Id) ?? App.Vault.Load(row.Id);
        if (task is not null)
            Frame.Navigate(typeof(TaskDetailPage), task);
    }

    private void PrevWeek_Click(object sender, RoutedEventArgs e)
    {
        _currentWeekStart = _currentWeekStart.AddDays(-7);
        RefreshLog();
    }

    private void NextWeek_Click(object sender, RoutedEventArgs e)
    {
        _currentWeekStart = _currentWeekStart.AddDays(7);
        RefreshLog();
    }

    private void ThisWeek_Click(object sender, RoutedEventArgs e)
    {
        _currentWeekStart = GetMondayOfWeek(DateTime.Today);
        RefreshLog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(_currentLog);
        Clipboard.SetContent(dp);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _workLog.GenerateAndSave(_currentWeekStart);
    }

    private static DateTime GetMondayOfWeek(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }
}

public sealed class CancelledTaskRow
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CancelledAtText { get; set; } = string.Empty;
    public string ReasonText { get; set; } = string.Empty;
    public string RestoreAccessibilityName { get; set; } = string.Empty;
    public string OpenAccessibilityName { get; set; } = string.Empty;

    public static CancelledTaskRow From(Glasswork.Core.Models.GlassworkTask task)
    {
        var cancelledAtText = task.CancelledAt.HasValue
            ? $"Cancelled {task.CancelledAt.Value.ToLocalTime():MMM d, yyyy 'at' h:mm tt}"
            : $"Cancellation time unavailable - Created {task.Created:MMM d, yyyy}";
        var reasonText = $"Reason: {task.CancellationReason ?? "Unavailable"}";
        return new CancelledTaskRow
        {
            Id = task.Id,
            Title = task.Title,
            CancelledAtText = cancelledAtText,
            ReasonText = reasonText,
            RestoreAccessibilityName = $"Restore {task.Title} to Backlog",
            OpenAccessibilityName = $"Open {task.Title}",
        };
    }
}
