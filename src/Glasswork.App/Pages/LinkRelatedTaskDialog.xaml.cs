using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Glasswork.Core.Research;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class LinkRelatedTaskDialog : ContentDialog
{
    private readonly IResearchCatalog _catalog;
    private readonly string _topicId;
    private readonly IReadOnlyList<ResearchTaskCandidateRow> _allTasks;

    public LinkRelatedTaskDialog(
        IndexService index,
        IResearchCatalog catalog,
        string topicId)
    {
        _catalog = catalog;
        _topicId = topicId;
        _allTasks = index.All
            .Where(task => !task.IsCancelled)
            .OrderBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => new ResearchTaskCandidateRow(
                task.Id,
                task.Title,
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                    task.Status.Replace('-', ' '))))
            .ToArray();
        InitializeComponent();
        ApplyFilter();
    }

    public ObservableCollection<ResearchTaskCandidateRow> Tasks { get; } = [];
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
        var matches = string.IsNullOrWhiteSpace(query)
            ? _allTasks
            : _allTasks.Where(task =>
                task.TaskId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || task.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        Tasks.Clear();
        foreach (var task in matches)
            Tasks.Add(task);
        TaskList.ItemsSource = Tasks;
        NoTaskMatches.Visibility = Tasks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        TaskList.SelectedItem = Tasks.FirstOrDefault();
    }

    private void OnLink(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (TaskList.SelectedItem is not ResearchTaskCandidateRow selected)
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

public sealed class ResearchTaskCandidateRow
{
    public ResearchTaskCandidateRow(
        string taskId,
        string title,
        string statusLabel)
    {
        TaskId = taskId;
        Title = title;
        StatusLabel = statusLabel;
    }

    public string TaskId { get; set; }
    public string Title { get; set; }
    public string StatusLabel { get; set; }
    public string AccessibleName => $"{Title}, {StatusLabel}, Task {TaskId}";
}
