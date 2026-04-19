using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        if (e.Parameter is GlassworkTask task)
        {
            App.TaskFileChangedExternally += OnFileChangedExternally;
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

        CreatedText.Text = $"Created: {task.Created:yyyy-MM-dd}";
        CompletedText.Text = task.CompletedAt.HasValue
            ? $"Completed: {task.CompletedAt.Value:yyyy-MM-dd HH:mm}"
            : "";
        IdText.Text = $"ID: {task.Id}";

        if (task.AdoLink.HasValue)
        {
            AdoPanel.Visibility = Visibility.Visible;
            AdoTitleRun.Text = $"#{task.AdoLink} — {task.AdoTitle ?? "linked"}";
        }
        else
        {
            AdoPanel.Visibility = Visibility.Collapsed;
        }

        _isLoading = false;
    }

    private void BindSubtasks(IList<SubTask> subtasks)
    {
        var active = subtasks.Where(s => !s.IsEffectivelyDone).ToList();
        var completed = subtasks.Where(s => s.IsEffectivelyDone).ToList();

        ActiveSubtaskList.ItemsSource = active;
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


    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.TaskFileChangedExternally -= OnFileChangedExternally;
        App.ActiveTask.Clear();
    }

    private void OnFileChangedExternally(object? sender, string fileName)
    {
        if (!App.ActiveTask.IsActive(fileName)) return;
        // Watcher fires on a thread-pool thread; show the banner on the UI thread.
        DispatcherQueue.TryEnqueue(() => ReloadBanner.IsOpen = true);
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
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        App.Vault.Delete(Task.Id);
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void OpenObsidian_Click(object sender, RoutedEventArgs e)
    {
        var uri = $"obsidian://open?vault=Wiki&file=todo%2F{Uri.EscapeDataString(Task.Id)}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void OpenAdo_Click(object sender, RoutedEventArgs e)
    {
        if (Task.AdoLink.HasValue)
        {
            // TODO: make org/project configurable
            var url = $"https://dev.azure.com/_workitems/edit/{Task.AdoLink.Value}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
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
