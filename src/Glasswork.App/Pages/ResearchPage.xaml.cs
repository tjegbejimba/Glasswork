using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
    private ResearchCatalogSnapshot _snapshot =
        new(Array.Empty<ResearchTopic>(), Array.Empty<ResearchCatalogDiagnostic>());
    private bool _isReconciling;
    private string? _selectedTopicId;
    private bool _suppressCatalogRefresh;

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
        App.Research.TopicsChanged += OnResearchTopicsChanged;
        RefreshCatalog(navigation?.TopicId, preserveCurrentState: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Research.TopicsChanged -= OnResearchTopicsChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnResearchTopicsChanged(
        object? sender,
        ResearchTopicsChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
            RefreshCatalog(requestedTopicId: null, preserveCurrentState: true));
    }

    private void RefreshCatalog(
        string? requestedTopicId = null,
        bool preserveCurrentState = true)
    {
        var type = (CatalogTypeFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var confidence = (CatalogConfidenceFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var freshness = ParseFreshness(
            (CatalogFreshnessFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        var result = App.Research.Search(new ResearchCatalogQuery(
            Text: CatalogSearchBox?.Text,
            WikiType: type,
            Confidence: confidence,
            Freshness: freshness));
        ApplySnapshot(
            new ResearchCatalogSnapshot(
                result.Topics,
                result.EligiblePages,
                result.Diagnostics),
            result.TotalTopicCount,
            requestedTopicId,
            preserveCurrentState);
    }

    private void ApplySnapshot(
        ResearchCatalogSnapshot snapshot,
        int totalTopicCount,
        string? requestedTopicId,
        bool preserveCurrentState)
    {
        var currentTopicId = preserveCurrentState ? _selectedTopicId : null;
        var state = ResearchPageRefreshPolicy.Resolve(
            snapshot,
            currentTopicId,
            requestedTopicId,
            preserveCurrentState ? TopicDetailScroll.VerticalOffset : 0);
        _snapshot = snapshot;
        ReconcileTopics(snapshot.Topics);

        var libraryIsEmpty = totalTopicCount == 0;
        var hasNoMatches = !libraryIsEmpty && Topics.Count == 0;
        PopulatedView.Visibility = libraryIsEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateView.Visibility = libraryIsEmpty ? Visibility.Visible : Visibility.Collapsed;
        TopicDetailView.Visibility = hasNoMatches ? Visibility.Collapsed : Visibility.Visible;
        NoResultsView.Visibility = hasNoMatches ? Visibility.Visible : Visibility.Collapsed;
        if (libraryIsEmpty || hasNoMatches)
        {
            _selectedTopic = null;
            TopicList.SelectedItem = null;
            return;
        }

        var selection = Topics.FirstOrDefault(row =>
                string.Equals(
                    row.Topic.Id,
                    state.TopicId,
                    StringComparison.OrdinalIgnoreCase))
            ?? Topics[0];
        _isReconciling = true;
        try
        {
            TopicList.SelectedItem = selection;
        }
        finally
        {
            _isReconciling = false;
        }
        if (!state.PreserveReadingPosition)
            TopicList.ScrollIntoView(selection);
        ShowTopic(selection.Topic, snapshot.Diagnostics);
        if (state.PreserveReadingPosition)
            RestoreReadingPosition(state.VerticalOffset, attempts: 0);
        else
            TopicDetailScroll.ChangeView(
                horizontalOffset: null,
                verticalOffset: 0,
                zoomFactor: null,
                disableAnimation: true);
    }

    private static ResearchFreshness? ParseFreshness(string? value) => value switch
    {
        "healthy" => ResearchFreshness.Healthy,
        "low" => ResearchFreshness.LowConfidence,
        "expired" => ResearchFreshness.Expired,
        "incomplete" => ResearchFreshness.Incomplete,
        _ => null,
    };

    private void CatalogFilter_Changed(object sender, object e)
    {
        if (!_suppressCatalogRefresh && TopicList is not null)
            RefreshCatalog();
    }

    private void ClearCatalogFilters_Click(object sender, RoutedEventArgs e)
    {
        ResetCatalogFilters();
        RefreshCatalog();
    }

    private void ResetCatalogFilters()
    {
        _suppressCatalogRefresh = true;
        try
        {
            CatalogSearchBox.Text = string.Empty;
            CatalogTypeFilter.SelectedIndex = 0;
            CatalogConfidenceFilter.SelectedIndex = 0;
            CatalogFreshnessFilter.SelectedIndex = 0;
        }
        finally
        {
            _suppressCatalogRefresh = false;
        }
    }

    private void ReconcileTopics(IReadOnlyList<ResearchTopic> topics)
    {
        _isReconciling = true;
        try
        {
            for (var targetIndex = 0; targetIndex < topics.Count; targetIndex++)
            {
                var topic = topics[targetIndex];
                var currentIndex = Topics
                    .Select((row, index) => (row, index))
                    .Where(pair => string.Equals(
                        pair.row.Topic.Id,
                        topic.Id,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.index)
                    .DefaultIfEmpty(-1)
                    .First();
                if (currentIndex < 0)
                {
                    Topics.Insert(targetIndex, new ResearchTopicRow(topic));
                    continue;
                }

                if (currentIndex != targetIndex)
                    Topics.Move(currentIndex, targetIndex);
                if (!ReferenceEquals(Topics[targetIndex].Topic, topic))
                    Topics[targetIndex] = new ResearchTopicRow(topic);
            }

            while (Topics.Count > topics.Count)
                Topics.RemoveAt(Topics.Count - 1);
        }
        finally
        {
            _isReconciling = false;
        }
    }

    private void TopicList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isReconciling
            && TopicList?.SelectedItem is ResearchTopicRow selected
            && TopicMarkdown is not null)
        {
            ShowTopic(selected.Topic, _snapshot.Diagnostics);
        }
    }

    private void ShowTopic(
        ResearchTopic topic,
        IReadOnlyList<ResearchCatalogDiagnostic> diagnostics)
    {
        _selectedTopic = topic;
        _selectedTopicId = topic.Id;
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
            ResearchFreshness.Incomplete => "Research freshness: incomplete metadata",
            _ => "Research freshness: healthy",
        };
        PrimaryKnowledgeAction.Content = topic.Freshness is
            ResearchFreshness.Expired or ResearchFreshness.LowConfidence
                ? "Refresh stale claims"
                : "Continue research";
        var warning = diagnostics.FirstOrDefault(diagnostic =>
            diagnostic.Code is ResearchCatalogDiagnosticCode.MalformedFrontmatter
                or ResearchCatalogDiagnosticCode.UnreadablePage
            && string.Equals(
                diagnostic.VaultRelativePath,
                topic.VaultRelativePath,
                StringComparison.OrdinalIgnoreCase));
        TopicWarning.IsOpen = warning is not null;
        TopicWarning.Message = warning is null
            ? string.Empty
            : BuildWarningMessage(warning);
        TopicMarkdown.Markdown = topic.Markdown;
    }

    private static string BuildWarningMessage(ResearchCatalogDiagnostic warning)
    {
        var detected = warning.DetectedOn.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        var preserved = warning.LastValidOn is { } lastValid
            ? $" Showing the last valid Topic snapshot from {lastValid:MMM d, yyyy}."
            : string.Empty;
        return $"Detected {detected}.{preserved} Repair the Wiki Page in Obsidian; a later valid save will replace this snapshot.";
    }

    private void RestoreReadingPosition(double verticalOffset, int attempts)
    {
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (verticalOffset > 0
                    && TopicDetailScroll.ScrollableHeight <= 0
                    && attempts < 5)
                {
                    RestoreReadingPosition(verticalOffset, attempts + 1);
                    return;
                }

                TopicDetailScroll.ChangeView(
                    horizontalOffset: null,
                    verticalOffset: Math.Min(
                        verticalOffset,
                        TopicDetailScroll.ScrollableHeight),
                    zoomFactor: null,
                    disableAnimation: true);
            });
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

    private async void AddTopicButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddResearchTopicDialog(App.Research)
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
        if (dialog.AddedTopic is null)
            return;

        _selectedTopicId = dialog.AddedTopic.Id;
        ResetCatalogFilters();
        RefreshCatalog(dialog.AddedTopic.Id, preserveCurrentState: false);
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
            ResearchFreshness.Incomplete => "Incomplete",
            _ => "Healthy",
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
