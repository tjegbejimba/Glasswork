using System.Collections.ObjectModel;
using System.Globalization;
using Glasswork.Core.Research;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glasswork.Pages;

public sealed partial class AgendaWikiPageDialog : ContentDialog
{
    private readonly IResearchCatalog _catalog;
    private readonly ObservableCollection<AgendaWikiPageRow> _pages = [];

    public AgendaWikiPageDialog(IResearchCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        EligiblePageList.ItemsSource = _pages;
        RefreshCandidates();
    }

    public ResearchPageCandidate? SelectedPage { get; private set; }

    private void PickerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EligiblePageList is not null)
            RefreshCandidates();
    }

    private void RefreshCandidates()
    {
        var result = _catalog.Search(new ResearchCatalogQuery(Text: PickerSearchBox?.Text));
        _pages.Clear();
        foreach (var page in result.EligiblePages)
            _pages.Add(new AgendaWikiPageRow(page));

        PickerCount.Text = $"{_pages.Count} Wiki Page{(_pages.Count == 1 ? string.Empty : "s")}";
        IsPrimaryButtonEnabled = false;
        PickerStatus.IsOpen = false;
    }

    private void EligiblePageList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var selected = EligiblePageList.SelectedItem as AgendaWikiPageRow;
        SelectedPage = selected?.Page;
        IsPrimaryButtonEnabled =
            selected?.Page.Eligibility == ResearchPageEligibility.Eligible;
        if (selected?.Page.Eligibility == ResearchPageEligibility.DuplicateStableId)
        {
            PickerStatus.Message =
                $"Stable Wiki Page id '{selected.Page.Id}' is duplicated. Resolve the duplicate before using this page.";
            PickerStatus.IsOpen = true;
        }
        else
        {
            PickerStatus.IsOpen = false;
        }
    }
}

public sealed class AgendaWikiPageRow
{
    public AgendaWikiPageRow(ResearchPageCandidate page)
    {
        Page = page;
        Title = page.Title;
        MetadataLine =
            $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(page.WikiType)} · " +
            page.VaultRelativePath;
        DuplicateIdVisibility =
            page.Eligibility == ResearchPageEligibility.DuplicateStableId
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    public ResearchPageCandidate Page { get; }
    public string Title { get; }
    public string MetadataLine { get; }
    public Visibility DuplicateIdVisibility { get; }
}
