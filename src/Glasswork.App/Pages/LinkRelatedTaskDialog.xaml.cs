using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Glasswork.Core.Research;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class LinkRelatedTaskDialog : ContentDialog
{
    private readonly IResearchCatalog _catalog;
    private readonly string _topicId;
    private readonly IReadOnlyList<TaskPickerRow> _allTasks;

    public LinkRelatedTaskDialog(
        IndexService index,
        IResearchCatalog catalog,
        string topicId)
    {
        _catalog = catalog;
        _topicId = topicId;
        var allTasks = index.All.ToArray();
        var candidates = allTasks
            .Where(task => !task.IsCancelled)
            .OrderBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToArray();
        _allTasks = TaskPickerPresentationPolicy.Project(allTasks, candidates);
        InitializeComponent();
        ApplyFilter();
    }

    public ObservableCollection<TaskPickerRow> Tasks { get; } = [];
    public string? LinkedTaskId { get; private set; }

    private void TaskSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e) =>
        ApplyFilterIfReady();

    private void ApplyFilterIfReady()
    {
        if (TaskList is not null && NoTaskMatches is not null)
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = TaskSearchBox?.Text?.Trim();
        var matches = TaskPickerPresentationPolicy.Filter(_allTasks, query);

        Tasks.Clear();
        foreach (var task in matches)
            Tasks.Add(task);
        TaskList.ItemsSource = Tasks;
        NoTaskMatches.Visibility = Tasks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TaskList.SelectedItem = Tasks.FirstOrDefault();
    }

    private void TaskList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container
            || args.Item is not TaskPickerRow row)
        {
            return;
        }

        AutomationProperties.SetName(container, row.AccessibleName);
        AutomationProperties.SetHelpText(container, row.FullAncestry ?? string.Empty);
    }

    private void OnLink(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (TaskList.SelectedItem is not TaskPickerRow selected)
        {
            args.Cancel = true;
            LinkError.Message = "Select a Task to link.";
            LinkError.IsOpen = true;
            return;
        }

        var result = _catalog.LinkExistingTask(_topicId, selected.TaskId);
        if (!result.Succeeded)
        {
            args.Cancel = true;
            LinkError.Message = result.Message;
            LinkError.IsOpen = true;
            return;
        }

        LinkedTaskId = selected.TaskId;
    }
}
