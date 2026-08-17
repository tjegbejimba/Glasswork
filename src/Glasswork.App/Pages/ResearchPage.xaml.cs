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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class ResearchPage : Page
{
    public ObservableCollection<ResearchTopicRow> Topics { get; } = [];
    public ObservableCollection<ResearchRelatedGroupRow> RelatedGroups { get; } = [];
    public ObservableCollection<ResearchContextWarningRow> ContextWarnings { get; } = [];

    private ResearchTopic? _selectedTopic;
    private IReadOnlyList<ResearchContextPage> _previewPages = [];
    private ResearchContextPage? _previewPage;
    private int _previewSelectedIndex = -1;
    private double _previewSynthesisVerticalOffset;
    private string? _previewInvokerPageId;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusRestoreTimer;
    private ResearchCatalogSnapshot _snapshot =
        new(Array.Empty<ResearchTopic>(), Array.Empty<ResearchCatalogDiagnostic>());
    private bool _isReconciling;

    public ResearchPage()
    {
        InitializeComponent();
        TopicList.ItemsSource = Topics;
        RelatedGroupsList.ItemsSource = RelatedGroups;
        ContextWarningsList.ItemsSource = ContextWarnings;
        TopicMarkdown.WikiLinkResolver = VaultPageHelper.BuildWikiLinkResolver();
        PreviewMarkdown.WikiLinkResolver = VaultPageHelper.BuildWikiLinkResolver();
        RootGrid.Children.Remove(PreviewDrawerOverlay);
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
        ClosePreviewDrawer(restoreFocus: false);
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
        ShowRelatedContext(topic.Context);
    }

    private void ShowRelatedContext(ResearchContext context)
    {
        RelatedGroups.Clear();
        foreach (var group in context.RelatedPages
                     .GroupBy(page => page.WikiType, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            RelatedGroups.Add(new ResearchRelatedGroupRow(group.Key, group));
        }

        ContextWarnings.Clear();
        foreach (var warning in context.Warnings)
            ContextWarnings.Add(new ResearchContextWarningRow(warning));

        RelatedContextPanel.Visibility =
            RelatedGroups.Count == 0 && ContextWarnings.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
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

    private async void RelatedPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResearchRelatedPageRow row }
            || _selectedTopic is null)
        {
            return;
        }

        _focusRestoreTimer?.Stop();
        _focusRestoreTimer = null;
        _previewPages = _selectedTopic.Context.RelatedPages;
        _previewSelectedIndex = _previewPages
            .Select((page, index) => (page, index))
            .Where(pair => string.Equals(
                pair.page.Id,
                row.Page.Id,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .First();
        if (_previewSelectedIndex < 0)
        {
            ShowRelatedContext(_selectedTopic.Context);
            return;
        }
        _previewSynthesisVerticalOffset = TopicDetailScroll.VerticalOffset;
        _previewInvokerPageId = row.Page.Id;
        ShowPreviewPage();
        var window = App.MainWindow as MainWindow
            ?? throw new InvalidOperationException("Research preview requires the app window.");
        PreviewDrawerOverlay.Visibility = Visibility.Visible;
        window.ShowModalOverlay(PreviewDrawerOverlay, PreviewCloseButton);
        PreviewDrawerOverlay.UpdateLayout();
        await FocusManager.TryFocusAsync(
            PreviewCloseButton,
            FocusState.Programmatic);
    }

    private void PreviewPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewSelectedIndex < 0)
            return;
        _previewSelectedIndex = Math.Max(0, _previewSelectedIndex - 1);
        ShowPreviewPage();
    }

    private void PreviewNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewSelectedIndex < 0)
            return;
        _previewSelectedIndex = Math.Min(
            _previewPages.Count - 1,
            _previewSelectedIndex + 1);
        ShowPreviewPage();
    }

    private void ShowPreviewPage()
    {
        if (_previewSelectedIndex < 0
            || _previewSelectedIndex >= _previewPages.Count)
            return;
        _previewPage = _previewPages[_previewSelectedIndex];
        PreviewTitle.Text = _previewPage.Title;
        PreviewMetadata.Text = ResearchRelatedPageRow.BuildMetadataLine(_previewPage);
        PreviewMarkdown.Markdown = _previewPage.Markdown;
        PreviewPreviousButton.IsEnabled = _previewSelectedIndex > 0;
        PreviewNextButton.IsEnabled = _previewSelectedIndex < _previewPages.Count - 1;
    }

    private void PreviewCloseButton_Click(object sender, RoutedEventArgs e) =>
        ClosePreviewDrawer(restoreFocus: true);

    private void ClosePreviewDrawer(bool restoreFocus)
    {
        if (_previewSelectedIndex < 0)
            return;

        var readingPosition = _previewSynthesisVerticalOffset;
        var invokerPageId = _previewInvokerPageId;
        PreviewDrawerOverlay.Visibility = Visibility.Collapsed;
        (App.MainWindow as MainWindow)?.HideModalOverlay(PreviewDrawerOverlay);
        _previewSelectedIndex = -1;
        _previewSynthesisVerticalOffset = 0;
        _previewPage = null;
        _previewPages = [];
        _previewInvokerPageId = null;
        BackgroundContent.UpdateLayout();
        TopicDetailScroll.ChangeView(
            horizontalOffset: null,
            verticalOffset: Math.Min(readingPosition, TopicDetailScroll.ScrollableHeight),
            zoomFactor: null,
            disableAnimation: true);
        if (restoreFocus)
        {
            var invoker = FindCurrentPreviewInvoker(invokerPageId)
                ?? TopicList;
            RestorePreviewInvokerFocus(invoker, attempts: 0);
        }
    }

    private Control? FindCurrentPreviewInvoker(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
            return null;

        return FindDescendants<Button>(RelatedGroupsList)
            .FirstOrDefault(button =>
                button.Tag is ResearchRelatedPageRow row
                && string.Equals(
                    row.Page.Id,
                    pageId,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper
            .GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper
                .GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private void RestorePreviewInvokerFocus(Control invoker, int attempts)
    {
        _focusRestoreTimer?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        _focusRestoreTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            invoker.StartBringIntoView();
            if (!invoker.Focus(FocusState.Programmatic))
            {
                await FocusManager.TryFocusAsync(
                    invoker,
                    FocusState.Programmatic);
            }

            var focusedElement = FocusManager.GetFocusedElement(XamlRoot);
            var restored = ReferenceEquals(focusedElement, invoker);
            if (!restored
                && attempts < 5)
            {
                RestorePreviewInvokerFocus(invoker, attempts + 1);
                return;
            }

            _focusRestoreTimer = null;
        };
        timer.Start();
    }

    private void PreviewEscape_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs e)
    {
        if (PreviewDrawerOverlay.Visibility != Visibility.Visible)
            return;
        ClosePreviewDrawer(restoreFocus: true);
        e.Handled = true;
    }

    private async void PreviewOpenInObsidianButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_previewPage is not null)
            await App.ObsidianLauncher.Open(_previewPage.VaultRelativePath);
    }

    private async void PreviewMarkdown_LinkClicked(
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

public sealed class ResearchRelatedGroupRow
{
    public ResearchRelatedGroupRow(
        string wikiType,
        IEnumerable<ResearchContextPage> pages)
    {
        TypeLabel = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(wikiType);
        Pages = pages
            .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .Select(page => new ResearchRelatedPageRow(page))
            .ToArray();
    }

    public string TypeLabel { get; }
    public IReadOnlyList<ResearchRelatedPageRow> Pages { get; }
}

public sealed class ResearchRelatedPageRow
{
    public ResearchRelatedPageRow(ResearchContextPage page)
    {
        Page = page;
        Title = page.Title;
        MetadataLine = BuildMetadataLine(page);
        RelationLabel = BuildRelationLabel(page.Relations);
    }

    public ResearchContextPage Page { get; }
    public string Title { get; }
    public string MetadataLine { get; }
    public string RelationLabel { get; }

    internal static string BuildMetadataLine(ResearchContextPage page)
    {
        var freshness = page.Freshness switch
        {
            ResearchFreshness.Expired => "Expired",
            ResearchFreshness.LowConfidence => "Low confidence",
            ResearchFreshness.Incomplete => "Incomplete",
            _ => "Healthy",
        };
        var updated = page.Updated?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
            ?? "date not set";
        return $"{freshness} · Updated {updated}";
    }

    private static string BuildRelationLabel(ResearchContextRelation relations)
    {
        var labels = new List<string>();
        if (relations.HasFlag(ResearchContextRelation.OutgoingWikiLink))
            labels.Add("Outgoing link");
        if (relations.HasFlag(ResearchContextRelation.Provenance))
            labels.Add("Provenance");
        if (relations.HasFlag(ResearchContextRelation.Backlink))
            labels.Add("Backlink");
        if (relations.HasFlag(ResearchContextRelation.IncludeOverride))
            labels.Add("Included");
        return string.Join(" · ", labels);
    }
}

public sealed class ResearchContextWarningRow
{
    public ResearchContextWarningRow(ResearchContextWarning warning)
    {
        Title = warning.Code switch
        {
            ResearchContextWarningCode.MissingPage =>
                $"Missing related page: {warning.Reference}",
            ResearchContextWarningCode.MalformedPage =>
                $"Unavailable related page: {warning.Reference}",
            ResearchContextWarningCode.ConflictingOverride =>
                $"Conflicting context override: {warning.Reference}",
            _ => $"Ambiguous related page: {warning.Reference}",
        };
        Message = warning.Message;
    }

    public string Title { get; }
    public string Message { get; }
}
