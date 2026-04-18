using System;
using System.Diagnostics;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class TaskDetailPage : Page
{
    public GlassworkTask Task { get; private set; } = new();

    private bool _isLoading;

    public TaskDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is GlassworkTask task)
        {
            _isLoading = true;
            Task = task;

            // Set combo boxes to match task state
            SetComboByTag(StatusBox, task.Status);
            SetComboByTag(PriorityBox, task.Priority);

            if (task.Due.HasValue)
                DueDatePicker.Date = new DateTimeOffset(task.Due.Value);

            SubtaskList.ItemsSource = task.Subtasks;

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

            _isLoading = false;
        }
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
