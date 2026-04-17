using System;
using Glasswork.Core.Models;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class BacklogPage : Page
{
    public BacklogViewModel ViewModel { get; }

    public BacklogPage()
    {
        ViewModel = new BacklogViewModel(App.Vault, App.Tasks);
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void StatusFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem item)
        {
            ViewModel.FilterStatus = item.Tag?.ToString() ?? "all";
        }
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateTaskDialog(App.Tasks) { XamlRoot = this.XamlRoot };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.CreatedTask is not null)
        {
            ViewModel.Refresh();
        }
    }

    private void ToggleMyDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.SelectedTask = task;
            ViewModel.ToggleMyDayCommand.Execute(null);
        }
    }

    private void SetTodo_Click(object sender, RoutedEventArgs e) => SetStatusFromMenu(sender, GlassworkTask.Statuses.Todo);
    private void SetInProgress_Click(object sender, RoutedEventArgs e) => SetStatusFromMenu(sender, GlassworkTask.Statuses.InProgress);
    private void SetDone_Click(object sender, RoutedEventArgs e) => SetStatusFromMenu(sender, GlassworkTask.Statuses.Done);

    private void SetStatusFromMenu(object sender, string status)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.SelectedTask = task;
            ViewModel.SetStatusCommand.Execute(status);
        }
    }

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GlassworkTask task)
        {
            Frame.Navigate(typeof(TaskDetailPage), task);
        }
    }

    private async void ImportAdo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AdoImportDialog { XamlRoot = this.XamlRoot };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.SelectedItem is { } ado)
        {
            var task = App.Tasks.CreateTask(ado.Title);
            task.AdoLink = ado.Id;
            task.AdoTitle = ado.Title;
            App.Vault.Save(task);
            ViewModel.Refresh();
        }
    }
}
