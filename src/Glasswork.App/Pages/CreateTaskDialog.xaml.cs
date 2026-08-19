using System;
using Glasswork.Core.Models;
using Glasswork.Core.Research;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class CreateTaskDialog : ContentDialog
{
    private readonly TaskService _taskService;
    private readonly IResearchCatalog? _researchCatalog;
    private readonly string? _researchTopicId;
    public GlassworkTask? CreatedTask { get; private set; }

    public CreateTaskDialog(TaskService taskService)
    {
        _taskService = taskService;
        InitializeComponent();
    }

    public CreateTaskDialog(
        TaskService taskService,
        IResearchCatalog researchCatalog,
        string researchTopicId)
    {
        _taskService = taskService;
        _researchCatalog = researchCatalog;
        _researchTopicId = researchTopicId;
        InitializeComponent();
        Title = "Create related Task";
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

        int? adoLink = null;
        var adoText = AdoLinkBox.Text?.Trim();
        if (!string.IsNullOrEmpty(adoText) && int.TryParse(adoText, out var parsed) && parsed > 0)
            adoLink = parsed;
        var adoTitle = string.IsNullOrWhiteSpace(AdoTitleBox.Text) ? null : AdoTitleBox.Text.Trim();

        var description = string.IsNullOrWhiteSpace(NotesBox.Text)
            ? null
            : NotesBox.Text;
        if (_researchCatalog is not null && _researchTopicId is not null)
        {
            var result = _researchCatalog.CreateRelatedTask(
                _researchTopicId,
                new ResearchTaskDraft(
                    title,
                    priority,
                    description,
                    AddToMyDayBox.IsChecked == true,
                    adoLink,
                    adoTitle));
            if (!result.Succeeded)
            {
                args.Cancel = true;
                CreateError.Message = result.Message;
                CreateError.IsOpen = true;
                return;
            }
            CreatedTask = App.Index.ById(result.Task!.TaskId);
            return;
        }

        CreatedTask = _taskService.CreateTask(
            title,
            priority,
            adoLink: adoLink,
            adoTitle: adoTitle,
            description: description,
            addToMyDay: AddToMyDayBox.IsChecked == true);
    }
}
