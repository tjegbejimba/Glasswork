using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Glasswork.Core.Research;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class AddResearchTopicDialog : ContentDialog
{
    private readonly IResearchCatalog _catalog;
    private readonly ObservableCollection<EligibleResearchPageRow> _pages = [];

    public AddResearchTopicDialog(IResearchCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        EligiblePageList.ItemsSource = _pages;
        RefreshCandidates();
    }

    public ResearchTopic? AddedTopic { get; private set; }

    private void PickerFilter_Changed(object sender, object e)
    {
        if (EligiblePageList is not null)
            RefreshCandidates();
    }

    private void RefreshCandidates()
    {
        var type = (PickerTypeFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var result = _catalog.Search(new ResearchCatalogQuery(
            Text: PickerSearchBox?.Text,
            WikiType: type));
        _pages.Clear();
        foreach (var page in result.EligiblePages)
            _pages.Add(new EligibleResearchPageRow(page));

        PickerCount.Text = $"{_pages.Count} eligible page{(_pages.Count == 1 ? string.Empty : "s")}";
        IsPrimaryButtonEnabled = false;
        PickerStatus.IsOpen = false;
    }

    private void EligiblePageList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var selected = EligiblePageList?.SelectedItem as EligibleResearchPageRow;
        IsPrimaryButtonEnabled = selected is not null
            && !selected.Page.IsOptedIn
            && selected.Page.Eligibility == ResearchPageEligibility.Eligible;
        if (selected?.Page.Eligibility == ResearchPageEligibility.DuplicateStableId)
        {
            PickerStatus.Message =
                $"Stable Wiki Page id '{selected.Page.Id}' is duplicated. Resolve the duplicate before adding this Topic.";
            PickerStatus.Severity = InfoBarSeverity.Error;
            PickerStatus.IsOpen = true;
        }
        else if (selected?.Page.IsOptedIn == true)
        {
            PickerStatus.Message = $"'{selected.Page.Title}' is already a Research Topic.";
            PickerStatus.Severity = InfoBarSeverity.Informational;
            PickerStatus.IsOpen = true;
        }
        else
        {
            PickerStatus.IsOpen = false;
        }
    }

    private void OnAddTopic(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (EligiblePageList.SelectedItem is not EligibleResearchPageRow selected)
        {
            ShowFailure("Select an eligible Wiki Page.");
            args.Cancel = true;
            return;
        }

        var result = _catalog.OptIn(selected.Page.VaultRelativePath);
        if (!result.Succeeded || result.Topic is null)
        {
            RefreshCandidates();
            ShowFailure(result.Message);
            args.Cancel = true;
            return;
        }

        AddedTopic = result.Topic;
    }

    private void ShowFailure(string message)
    {
        PickerStatus.Message = message;
        PickerStatus.Severity = InfoBarSeverity.Error;
        PickerStatus.IsOpen = true;
    }
}

public sealed class EligibleResearchPageRow
{
    public EligibleResearchPageRow(ResearchPageCandidate page)
    {
        Page = page;
        Title = page.Title;
        MetadataLine =
            $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(page.WikiType)} · " +
            $"{page.Id} · Confidence {page.Confidence ?? "not set"}";
        OptedInVisibility = page.IsOptedIn ? Visibility.Visible : Visibility.Collapsed;
        DuplicateIdVisibility = page.Eligibility == ResearchPageEligibility.DuplicateStableId
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public ResearchPageCandidate Page { get; }
    public string Title { get; }
    public string MetadataLine { get; }
    public Visibility OptedInVisibility { get; }
    public Visibility DuplicateIdVisibility { get; }
}
