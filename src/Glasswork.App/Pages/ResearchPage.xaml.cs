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
        var snapshot = navigation?.Snapshot
            ?? App.Research.Capture(DateOnly.FromDateTime(DateTime.Today));
        ApplySnapshot(snapshot, navigation?.TopicId, preserveCurrentState: false);
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
            ApplySnapshot(e.Snapshot, requestedTopicId: null, preserveCurrentState: true));
    }

    private void ApplySnapshot(
        ResearchCatalogSnapshot snapshot,
        string? requestedTopicId,
        bool preserveCurrentState)
    {
        var currentTopicId = preserveCurrentState ? _selectedTopic?.Id : null;
        var state = ResearchPageRefreshPolicy.Resolve(
            snapshot,
            currentTopicId,
            requestedTopicId,
            preserveCurrentState ? TopicDetailScroll.VerticalOffset : 0);
        _snapshot = snapshot;
        ReconcileTopics(snapshot.Topics);

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
