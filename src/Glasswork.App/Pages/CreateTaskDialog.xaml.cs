using System;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class CreateTaskDialog : ContentDialog
{
    private readonly TaskService _taskService;
    public GlassworkTask? CreatedTask { get; private set; }

    public CreateTaskDialog(TaskService taskService)
    {
        _taskService = taskService;
        InitializeComponent();
    }

    private void OnCreate(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var title = TitleBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            args.Cancel = true;
            return;
        }

        var priority = (PriorityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium";
        var task = _taskService.CreateTask(title, priority);

        if (!string.IsNullOrWhiteSpace(NotesBox.Text))
        {
            task.Body = NotesBox.Text;
            App.Vault.Save(task);
        }

        if (AddToMyDayBox.IsChecked == true)
        {
            _taskService.ToggleMyDay(task);
        }

        CreatedTask = task;
    }
}
