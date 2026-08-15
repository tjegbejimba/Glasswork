using System;
using System.Collections.ObjectModel;
using Glasswork.Core.Services;
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

    public ObservableCollection<CancelledTaskRow> CancelledTasks { get; } = [];

    public WorkLogPage()
    {
        _workLog = new WorkLogService(App.Vault, App.TaskQuery);
        InitializeComponent();
        WorkLogTabs.SelectedIndex = string.Equals(
            App.UiState.Get<string>(App.WorkLogSelectedTabKey),
            "cancelled",
            StringComparison.Ordinal)
                ? 1
                : 0;
        _isInitialized = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _currentWeekStart = GetMondayOfWeek(DateTime.Today);
        App.Index.TasksChanged += OnTasksChanged;
        RefreshSelectedTab();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Index.TasksChanged -= OnTasksChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnTasksChanged(object? sender, TasksChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(RefreshSelectedTab);
    }

    private void WorkLogTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;

        App.UiState.Set(
            App.WorkLogSelectedTabKey,
            WorkLogTabs.SelectedIndex == 1 ? "cancelled" : "completed");
        RefreshSelectedTab();
    }

    private void RefreshSelectedTab()
    {
        if (WorkLogTabs.SelectedIndex == 1)
            RefreshCancelledTasks();
        else
            RefreshLog();
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
        };
    }
}
