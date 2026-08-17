using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Glasswork.Controls;
using Glasswork.Core.Research;
using Glasswork.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class ResearchPage : Page
{
    public ObservableCollection<ResearchTopicRow> Topics { get; } = [];

    private ResearchTopic? _selectedTopic;

    public ResearchPage()
    {
        InitializeComponent();
        TopicList.ItemsSource = Topics;
        TopicMarkdown.WikiLinkResolver = VaultPageHelper.BuildWikiLinkResolver();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var navigation = e.Parameter as ResearchPageNavigation;
        LoadSnapshot(
            navigation?.Snapshot ?? App.Research.Capture(),
            navigation?.TopicId);
    }

    private void LoadSnapshot(ResearchCatalogSnapshot snapshot, string? selectedTopicId)
    {
        Topics.Clear();
        foreach (var topic in snapshot.Topics)
            Topics.Add(new ResearchTopicRow(topic));

        var isEmpty = Topics.Count == 0;
        PopulatedView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateView.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (isEmpty)
        {
            _selectedTopic = null;
            TopicList.SelectedItem = null;
            return;
        }

        var selection = Topics.FirstOrDefault(row =>
                string.Equals(row.Topic.Id, selectedTopicId, StringComparison.OrdinalIgnoreCase))
            ?? Topics[0];
        TopicList.SelectedItem = selection;
        TopicList.ScrollIntoView(selection);
        ShowTopic(selection.Topic);
    }

    private void TopicList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (TopicList?.SelectedItem is ResearchTopicRow selected
            && TopicMarkdown is not null)
        {
            ShowTopic(selected.Topic);
        }
    }

    private void ShowTopic(ResearchTopic topic)
    {
        _selectedTopic = topic;
        TopicTitle.Text = topic.Title;
        TopicType.Text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(topic.WikiType);
        TopicConfidence.Text = $"Confidence: {topic.Confidence ?? "not set"}";
        TopicUpdated.Text = topic.Updated is { } updated
            ? $"Updated {updated:MMM d, yyyy}"
            : "Updated date not set";
        TopicExpires.Text = topic.Expires is { } expires
            ? $"{(topic.Freshness == ResearchFreshness.Expired ? "Expired" : "Expires")} {expires:MMM d, yyyy}"
            : "No expiry";
        TopicFreshness.Text = topic.Freshness switch
        {
            ResearchFreshness.Expired => "Research freshness: expired",
            ResearchFreshness.LowConfidence => "Research freshness: low confidence",
            _ => "Research freshness: current",
        };
        TopicMarkdown.Markdown = topic.Markdown;
    }

    private async void TopicMarkdown_LinkClicked(
        object? sender,
        LinkClickedEventArgs e)
    {
        await VaultPageHelper.RouteLinkClickAsync(Frame, e);
    }

    private async void OpenInObsidianButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTopic is not null)
            await App.ObsidianLauncher.Open(_selectedTopic.VaultRelativePath);
    }
}

public sealed record ResearchPageNavigation(
    ResearchCatalogSnapshot Snapshot,
    string? TopicId);

public sealed class ResearchTopicRow
{
    public ResearchTopicRow(ResearchTopic topic)
    {
        Topic = topic;
        Title = topic.Title;
        Summary = topic.Summary;
        FreshnessLabel = topic.Freshness switch
        {
            ResearchFreshness.Expired => "Expired",
            ResearchFreshness.LowConfidence => "Low confidence",
            _ => "Current",
        };
        var updated = topic.Updated?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            ?? "date not set";
        MetadataLine =
            $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(topic.WikiType)} · " +
            $"Confidence {topic.Confidence ?? "not set"} · Updated {updated}";
        AccessibleStatus = $"{FreshnessLabel}. {MetadataLine}";
    }

    public ResearchTopic Topic { get; }
    public string Title { get; }
    public string Summary { get; }
    public string FreshnessLabel { get; }
    public string MetadataLine { get; }
    public string AccessibleStatus { get; }
}
