using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Glasswork.Controls;
using Glasswork.Core.Models;
using Glasswork.Core.Research;
using Glasswork.Core.Services;
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
    public ObservableCollection<ResearchContextSelectionRow> ContextSelectionRows { get; } = [];
    public ObservableCollection<ResearchRelatedTaskRow> ActiveRelatedTasks { get; } = [];
    public ObservableCollection<ResearchRelatedTaskRow> CompletedRelatedTasks { get; } = [];
    public ObservableCollection<ResearchRelatedWorkWarningRow> RelatedWorkWarnings { get; } = [];

    private ResearchTopic? _selectedTopic;
    private IReadOnlyList<ResearchContextPage> _previewPages = [];
    private ResearchContextPage? _previewPage;
    private int _previewSelectedIndex = -1;
    private double _previewSynthesisVerticalOffset;
    private string? _previewTopicId;
    private string? _previewInvokerPageId;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusRestoreTimer;
    private int _focusRestoreGeneration;
    private ResearchCatalogSnapshot _snapshot =
        new(Array.Empty<ResearchTopic>(), Array.Empty<ResearchCatalogDiagnostic>());
    private bool _isReconciling;
    private string? _selectedTopicId;
    private string? _pendingRemovalTopicId;
    private Control? _removeTopicInvoker;
    private bool _suppressCatalogRefresh;
    private ResearchDrawerMode _drawerMode;
    private Button? _contextSelectionInvoker;
    private bool _suppressContextDrawerRefreshClose;
    private IReadOnlyList<ResearchCandidateProjection> _durableCandidateProjection = [];

    public ResearchPage()
    {
        InitializeComponent();
        TopicList.ItemsSource = Topics;
        RelatedGroupsList.ItemsSource = RelatedGroups;
        ContextWarningsList.ItemsSource = ContextWarnings;
        ContextSelectionList.ItemsSource = ContextSelectionRows;
        ActiveRelatedWorkList.ItemsSource = ActiveRelatedTasks;
        CompletedRelatedWorkList.ItemsSource = CompletedRelatedTasks;
        RelatedWorkWarningsList.ItemsSource = RelatedWorkWarnings;
        TopicMarkdown.WikiLinkResolver = VaultPageHelper.BuildWikiLinkResolver();
        PreviewMarkdown.WikiLinkResolver = VaultPageHelper.BuildWikiLinkResolver();
        RootGrid.Children.Remove(PreviewDrawerOverlay);
        RootGrid.Children.Remove(RemoveTopicOverlay);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var navigation = e.Parameter as ResearchPageNavigation;
        App.Research.TopicsChanged += OnResearchTopicsChanged;
        App.Index.TasksChanged += OnTasksChanged;
        RefreshCatalog(navigation?.TopicId, preserveCurrentState: false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CloseRemoveTopicOverlay();
        CloseOpenDrawer(restoreFocus: false);
        App.Research.TopicsChanged -= OnResearchTopicsChanged;
        App.Index.TasksChanged -= OnTasksChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnTasksChanged(object? sender, TasksChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
            RefreshCatalog(requestedTopicId: null, preserveCurrentState: true));
    }

    private void OnResearchTopicsChanged(
        object? sender,
        ResearchTopicsChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var selectedTopicUnchanged = _selectedTopic is not null
                && e.Snapshot.Topics.FirstOrDefault(topic => string.Equals(
                    topic.Id,
                    _selectedTopic.Id,
                    StringComparison.OrdinalIgnoreCase)) is { } refreshedTopic
                && ReferenceEquals(refreshedTopic, _selectedTopic);
            var durableProjectionUnchanged =
                _drawerMode != ResearchDrawerMode.DurableCuration
                || _selectedTopic is not null
                && _durableCandidateProjection.SequenceEqual(
                    BuildCandidateProjection(
                       e.Snapshot.EligiblePages,
                       _selectedTopic.Id));
            var preserveOpenDrawer = (_drawerMode is ResearchDrawerMode.SessionSelection
                    or ResearchDrawerMode.DurableCuration)
                && selectedTopicUnchanged
                && durableProjectionUnchanged;
            _suppressContextDrawerRefreshClose = preserveOpenDrawer;
            try
            {
                RefreshCatalog(requestedTopicId: null, preserveCurrentState: true);
            }
            finally
            {
                _suppressContextDrawerRefreshClose = false;
            }
        });
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
            ShowRelatedContext(ResearchContext.Empty);
            ShowRelatedWork(ResearchRelatedWork.Empty, resetCompletedExpansion: true);
            ReconcileOpenPreview();
            ReconcileOpenContextSelectionDrawer();
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
        ReconcileOpenPreview();
        ReconcileOpenContextSelectionDrawer();
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
        var topicChanged = !string.Equals(
            _selectedTopic?.Id,
            topic.Id,
            StringComparison.OrdinalIgnoreCase);
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
        ShowRelatedContext(topic.Context);
        ShowRelatedWork(topic.RelatedWork, topicChanged);
        UpdateContextSummary(topic);
    }

    private void ShowRelatedWork(
        ResearchRelatedWork relatedWork,
        bool resetCompletedExpansion)
    {
        var repairableReferences = relatedWork.Warnings
            .Where(warning => warning.CanRepair)
            .Select(warning => warning.Reference)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ActiveRelatedTasks.Clear();
        foreach (var task in relatedWork.ActiveTasks)
        {
            ActiveRelatedTasks.Add(new ResearchRelatedTaskRow(
                task,
                repairableReferences.Contains(task.TaskId)));
        }

        CompletedRelatedTasks.Clear();
        foreach (var task in relatedWork.CompletedTasks)
        {
            CompletedRelatedTasks.Add(new ResearchRelatedTaskRow(
                task,
                repairableReferences.Contains(task.TaskId)));
        }

        RelatedWorkWarnings.Clear();
        foreach (var warning in relatedWork.Warnings)
            RelatedWorkWarnings.Add(new ResearchRelatedWorkWarningRow(warning));

        RelatedWorkEmpty.Visibility =
            ActiveRelatedTasks.Count == 0 && CompletedRelatedTasks.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        ActiveRelatedWorkSection.Visibility = ActiveRelatedTasks.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompletedRelatedWorkExpander.Visibility = CompletedRelatedTasks.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompletedRelatedWorkHeader.Text =
            $"Completed or closed ({CompletedRelatedTasks.Count})";
        if (resetCompletedExpansion)
            CompletedRelatedWorkExpander.IsExpanded = false;
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

    private void UpdateContextSummary(ResearchTopic topic)
    {
        var total = topic.Context.RelatedPages.Count + 1;
        var prepared = App.Research.PreparedSessionContext;
        var selected = string.Equals(
                prepared?.TopicId,
                topic.Id,
                StringComparison.OrdinalIgnoreCase)
            ? prepared!.PageIds.Count
            : total;
        ContextSummary.Text = $"{selected} of {total} context pages";
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

        _focusRestoreGeneration++;
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
        _previewTopicId = _selectedTopic.Id;
        _previewInvokerPageId = row.Page.Id;
        ShowPreviewPage();
        _drawerMode = ResearchDrawerMode.Preview;
        PreviewDrawerContent.Visibility = Visibility.Visible;
        ContextSelectionDrawerContent.Visibility = Visibility.Collapsed;
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

    private void ClosePreviewDrawer(
        bool restoreFocus,
        bool restoreReadingPosition = true,
        Control? focusTarget = null)
    {
        if (_drawerMode != ResearchDrawerMode.Preview || _previewSelectedIndex < 0)
            return;

        var focusRestoreGeneration = ++_focusRestoreGeneration;
        _focusRestoreTimer?.Stop();
        _focusRestoreTimer = null;
        var readingPosition = _previewSynthesisVerticalOffset;
        var invokerPageId = _previewInvokerPageId;
        PreviewDrawerOverlay.Visibility = Visibility.Collapsed;
        (App.MainWindow as MainWindow)?.HideModalOverlay(PreviewDrawerOverlay);
        _previewSelectedIndex = -1;
        _previewSynthesisVerticalOffset = 0;
        _previewPage = null;
        _previewPages = [];
        _previewTopicId = null;
        _previewInvokerPageId = null;
        _drawerMode = ResearchDrawerMode.None;
        BackgroundContent.UpdateLayout();
        TopicDetailScroll.ChangeView(
            horizontalOffset: null,
            verticalOffset: restoreReadingPosition
                ? Math.Min(readingPosition, TopicDetailScroll.ScrollableHeight)
                : 0,
            zoomFactor: null,
            disableAnimation: true);
        if (restoreFocus)
        {
            var invoker = focusTarget
                ?? FindCurrentPreviewInvoker(invokerPageId)
                ?? (EmptyStateView.Visibility == Visibility.Visible
                    ? ResearchEmptyAddTopicButton
                    : TopicList);
            RestorePreviewInvokerFocus(
                invoker,
                attempts: 0,
                generation: focusRestoreGeneration);
        }
    }

    private void ReconcileOpenPreview()
    {
        if (_previewSelectedIndex < 0)
            return;

        if (_selectedTopic is null)
        {
            Control focusTarget = EmptyStateView.Visibility == Visibility.Visible
                ? ResearchEmptyAddTopicButton
                : CatalogSearchBox;
            ClosePreviewDrawer(
                restoreFocus: true,
                restoreReadingPosition: false,
                focusTarget: focusTarget);
            return;
        }

        if (!string.Equals(
                _previewTopicId,
                _selectedTopic.Id,
                StringComparison.OrdinalIgnoreCase)
            || _previewPage is null)
        {
            ClosePreviewDrawer(
                restoreFocus: true,
                restoreReadingPosition: false,
                focusTarget: TopicList);
            return;
        }

        var currentPages = _selectedTopic.Context.RelatedPages;
        var currentIndex = currentPages
            .Select((page, index) => (page, index))
            .Where(pair => string.Equals(
                pair.page.Id,
                _previewPage.Id,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .First();
        if (currentIndex < 0)
        {
            ClosePreviewDrawer(restoreFocus: true);
            return;
        }

        _previewPages = currentPages;
        _previewSelectedIndex = currentIndex;
        ShowPreviewPage();
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

    private void RestorePreviewInvokerFocus(
        Control invoker,
        int attempts,
        int generation)
    {
        if (generation != _focusRestoreGeneration)
            return;

        _focusRestoreTimer?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        _focusRestoreTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _focusRestoreGeneration)
                return;

            invoker.StartBringIntoView();
            _ = invoker.Focus(FocusState.Programmatic);
            var focusedElement = FocusManager.GetFocusedElement(XamlRoot);
            var restored = ReferenceEquals(focusedElement, invoker);
            if (!restored
                && attempts < 5)
            {
                RestorePreviewInvokerFocus(
                    invoker,
                    attempts + 1,
                    generation);
                return;
            }

            if (ReferenceEquals(_focusRestoreTimer, timer))
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
        CloseOpenDrawer(restoreFocus: true);
        e.Handled = true;
    }

    private void CloseOpenDrawer(bool restoreFocus)
    {
        if (_drawerMode == ResearchDrawerMode.Preview)
        {
            ClosePreviewDrawer(restoreFocus);
            return;
        }
        if (_drawerMode is ResearchDrawerMode.SessionSelection
            or ResearchDrawerMode.DurableCuration)
        {
            CloseContextSelectionDrawer(restoreFocus);
        }
    }

    private async void PrimaryKnowledgeAction_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTopic is not null && sender is Button button)
            await OpenContextSelectionDrawer(ResearchDrawerMode.SessionSelection, button);
    }

    private async void CurateContextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTopic is not null && sender is Button button)
            await OpenContextSelectionDrawer(ResearchDrawerMode.DurableCuration, button);
    }

    private async System.Threading.Tasks.Task OpenContextSelectionDrawer(
        ResearchDrawerMode mode,
        Button invoker)
    {
        if (_selectedTopic is null)
            return;

        _drawerMode = mode;
        _contextSelectionInvoker = invoker;
        ContextSelectionRows.Clear();
        var contextIds = _selectedTopic.Context.RelatedPages
            .Select(page => page.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ContextSelectionRows.Add(ResearchContextSelectionRow.Topic(_selectedTopic, mode));
        if (mode == ResearchDrawerMode.SessionSelection)
        {
            foreach (var page in _selectedTopic.Context.RelatedPages)
            {
                ContextSelectionRows.Add(
                    ResearchContextSelectionRow.Related(page, isSelected: true, mode));
            }
            ContextSelectionEyebrow.Text = "Research Session";
            ContextSelectionTitle.Text = "Choose context for the next session";
            ContextSelectionExplanation.Text =
                "All current context pages are selected. Deselect optional pages to narrow only the next Research Session; durable Research context will not change.";
            ContextSelectionDoneButton.Content = "Keep for next session";
        }
        else
        {
            var durableCandidates = App.Research.Capture().EligiblePages
                .Where(page =>
                    page.Eligibility == ResearchPageEligibility.Eligible
                    && !string.Equals(
                        page.Id,
                        _selectedTopic.Id,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(page => page.WikiType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(page => page.Id, StringComparer.Ordinal)
                .ToArray();
            _durableCandidateProjection = durableCandidates
                .Select(ResearchCandidateProjection.From)
                .ToArray();
            foreach (var page in durableCandidates)
            {
                ContextSelectionRows.Add(
                    ResearchContextSelectionRow.Candidate(
                        page,
                        contextIds.Contains(page.Id),
                        mode));
            }
            ContextSelectionEyebrow.Text = "Durable Research context";
            ContextSelectionTitle.Text = "Curate default context";
            ContextSelectionExplanation.Text =
                "Choose the eligible Wiki Pages that normally ground this Topic. Changes update only glasswork.research metadata; Wiki links and page prose remain unchanged.";
            ContextSelectionDoneButton.Content = "Done";
        }
        ContextSelectionError.IsOpen = false;
        UpdateContextSelectionSummary();
        PreviewDrawerContent.Visibility = Visibility.Collapsed;
        ContextSelectionDrawerContent.Visibility = Visibility.Visible;
        var window = App.MainWindow as MainWindow
            ?? throw new InvalidOperationException("Research context selection requires the app window.");
        PreviewDrawerOverlay.Visibility = Visibility.Visible;
        window.ShowModalOverlay(PreviewDrawerOverlay, ContextSelectionCloseButton);
        PreviewDrawerOverlay.UpdateLayout();
        foreach (var checkBox in FindDescendants<CheckBox>(ContextSelectionList))
        {
            if (checkBox.Tag is ResearchContextSelectionRow row)
                checkBox.IsChecked = row.IsSelected;
        }
        await FocusManager.TryFocusAsync(
            ContextSelectionCloseButton,
            FocusState.Programmatic);
    }

    private void ContextSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox
            {
                Tag: ResearchContextSelectionRow row,
                IsChecked: bool isChecked,
            }
            || !row.IsEnabled
            || _selectedTopic is null)
        {
            return;
        }

        row.IsSelected = isChecked;
        if (_drawerMode == ResearchDrawerMode.DurableCuration)
        {
            var result = App.Research.SetContextPageIncluded(
                _selectedTopic.Id,
                row.Id,
                isChecked);
            if (!result.Succeeded)
            {
                row.IsSelected = !isChecked;
                ((CheckBox)sender).IsChecked = row.IsSelected;
                ContextSelectionError.Message = result.Message;
                ContextSelectionError.IsOpen = true;
                return;
            }
            ContextSelectionError.IsOpen = false;
            _selectedTopic = result.Topic;
            _selectedTopicId = result.Topic!.Id;
            _suppressContextDrawerRefreshClose = true;
            try
            {
                RefreshCatalog(result.Topic.Id, preserveCurrentState: true);
            }
            finally
            {
                _suppressContextDrawerRefreshClose = false;
            }
        }
        UpdateContextSelectionSummary();
    }

    private void UpdateContextSelectionSummary()
    {
        var optional = ContextSelectionRows.Where(row => !row.IsTopic).ToArray();
        var selected = optional.Count(row => row.IsSelected == true);
        ContextSelectionSummary.Text = _drawerMode == ResearchDrawerMode.SessionSelection
            ? $"{selected} of {optional.Length} optional pages selected"
            : $"{selected} of {optional.Length} eligible pages included";
    }

    private void ContextSelectionDoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTopic is null)
            return;
        if (_drawerMode == ResearchDrawerMode.SessionSelection)
        {
            var selectedIds = ContextSelectionRows
                .Where(row => !row.IsTopic && row.IsSelected == true)
                .Select(row => row.Id)
                .ToArray();
            var result = App.Research.PrepareSessionContext(_selectedTopic.Id, selectedIds);
            if (!result.Succeeded)
            {
                ContextSelectionError.Message = result.Message;
                ContextSelectionError.IsOpen = true;
                return;
            }
            UpdateContextSummary(_selectedTopic);
        }
        CloseContextSelectionDrawer(restoreFocus: true);
    }

    private void ContextSelectionCloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseContextSelectionDrawer(restoreFocus: true);

    private void CloseContextSelectionDrawer(bool restoreFocus)
    {
        if (_drawerMode is not (ResearchDrawerMode.SessionSelection
            or ResearchDrawerMode.DurableCuration))
        {
            return;
        }

        var invoker = _contextSelectionInvoker;
        PreviewDrawerOverlay.Visibility = Visibility.Collapsed;
        (App.MainWindow as MainWindow)?.HideModalOverlay(PreviewDrawerOverlay);
        ContextSelectionDrawerContent.Visibility = Visibility.Collapsed;
        ContextSelectionRows.Clear();
        _durableCandidateProjection = [];
        _contextSelectionInvoker = null;
        _drawerMode = ResearchDrawerMode.None;
        if (restoreFocus && invoker is not null)
            _ = invoker.Focus(FocusState.Programmatic);
    }

    private void ReconcileOpenContextSelectionDrawer()
    {
        if (_suppressContextDrawerRefreshClose
            || _drawerMode is not (ResearchDrawerMode.SessionSelection
                or ResearchDrawerMode.DurableCuration))
        {
            return;
        }

        CloseContextSelectionDrawer(restoreFocus: true);
    }

    private static IReadOnlyList<ResearchCandidateProjection> BuildCandidateProjection(
        IReadOnlyList<ResearchPageCandidate> candidates,
        string topicId) =>
        candidates
            .Where(page =>
                page.Eligibility == ResearchPageEligibility.Eligible
                && !string.Equals(
                    page.Id,
                    topicId,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(page => page.WikiType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(page => page.Id, StringComparer.Ordinal)
            .Select(ResearchCandidateProjection.From)
            .ToArray();

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

    private async void CreateRelatedTaskButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedTopic is null)
            return;
        var dialog = new CreateTaskDialog(
            App.Tasks,
            App.Research,
            _selectedTopic.Id)
        {
            XamlRoot = XamlRoot,
        };
        dialog.WithAppTheme(this);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary
            && dialog.CreatedTask is not null)
        {
            RefreshCatalog(_selectedTopic.Id, preserveCurrentState: true);
        }
    }

    private async void LinkExistingTaskButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedTopic is null)
            return;
        var dialog = new LinkRelatedTaskDialog(
            App.Index,
            App.Research,
            _selectedTopic.Id)
        {
            XamlRoot = XamlRoot,
        };
        dialog.WithAppTheme(this);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary
            && dialog.LinkedTaskId is not null)
        {
            RefreshCatalog(_selectedTopic.Id, preserveCurrentState: true);
        }
    }

    private void RelatedTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResearchRelatedTaskRow row })
            return;
        var task = App.Index.ById(row.Task.TaskId);
        if (task is not null)
            Frame.Navigate(typeof(TaskDetailPage), task);
    }

    private async void RepairRelatedTaskButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedTopic is null
            || sender is not Button { Tag: ResearchRelatedTaskRow row })
        {
            return;
        }

        var result = App.Research.RepairRelatedTask(
            _selectedTopic.Id,
            row.Task.TaskId);
        if (!result.Succeeded)
        {
            var error = new ContentDialog
            {
                Title = "Unable to repair Related Work",
                Content = result.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            error.WithAppTheme(this);
            await error.ShowAsync();
            return;
        }
        RefreshCatalog(_selectedTopic.Id, preserveCurrentState: true);
    }

    private void RemoveFromResearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTopic is not { } topic)
            return;

        _pendingRemovalTopicId = topic.Id;
        _removeTopicInvoker = sender as Control ?? RemoveFromResearchButton;
        RemoveTopicPreserveMessage.Text =
            $"\"{topic.Title}\" will remain as a Wiki Page in your Vault.";
        RemoveTopicOverlay.Visibility = Visibility.Visible;
        var window = App.MainWindow as MainWindow
            ?? throw new InvalidOperationException("Research removal requires the app window.");
        window.ShowModalOverlay(RemoveTopicOverlay, RemoveTopicCancelButton);
        RemoveTopicOverlay.UpdateLayout();
        _ = FocusManager.TryFocusAsync(
            RemoveTopicCancelButton,
            FocusState.Programmatic);
    }

    private void RemoveTopicCancelButton_Click(object sender, RoutedEventArgs e) =>
        CloseRemoveTopicOverlay(restoreFocus: true);

    private void RemoveTopicEscape_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs e)
    {
        if (RemoveTopicOverlay.Visibility != Visibility.Visible)
            return;
        CloseRemoveTopicOverlay(restoreFocus: true);
        e.Handled = true;
    }

    private async void RemoveTopicConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var topicId = _pendingRemovalTopicId;
        var removalInvoker = _removeTopicInvoker;
        CloseRemoveTopicOverlay();
        if (string.IsNullOrWhiteSpace(topicId))
            return;

        var result = App.Research.Remove(topicId);
        if (!result.Succeeded)
        {
            var error = new ContentDialog
            {
                Title = "Unable to remove Research Topic",
                Content = result.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            error.WithAppTheme(this);
            await error.ShowAsync();
            if (removalInvoker is not null)
                RestoreRemovalFocus(removalInvoker);
            return;
        }

        RefreshCatalog(requestedTopicId: null, preserveCurrentState: true);
        RestorePostRemovalFocus();
    }

    private void CloseRemoveTopicOverlay(bool restoreFocus = false)
    {
        var invoker = _removeTopicInvoker;
        if (RemoveTopicOverlay.Visibility != Visibility.Visible)
        {
            _pendingRemovalTopicId = null;
            _removeTopicInvoker = null;
            return;
        }

        RemoveTopicOverlay.Visibility = Visibility.Collapsed;
        (App.MainWindow as MainWindow)?.HideModalOverlay(RemoveTopicOverlay);
        _pendingRemovalTopicId = null;
        _removeTopicInvoker = null;
        if (restoreFocus && invoker is not null)
            RestoreRemovalFocus(invoker);
    }

    private void RestorePostRemovalFocus()
    {
        TopicList.UpdateLayout();
        var target = EmptyStateView.Visibility == Visibility.Visible
            ? ResearchEmptyAddTopicButton
            : TopicList.ContainerFromItem(TopicList.SelectedItem) as Control
                ?? TopicList;
        RestoreRemovalFocus(target);
    }

    private void RestoreRemovalFocus(Control target)
    {
        var generation = ++_focusRestoreGeneration;
        RestorePreviewInvokerFocus(
            target,
            attempts: 0,
            generation: generation);
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

    private sealed record ResearchCandidateProjection(
        string Id,
        string Title,
        string WikiType,
        string? Confidence,
        DateOnly? Updated,
        DateOnly? Expires,
        ResearchFreshness Freshness,
        string VaultRelativePath,
        bool IsOptedIn)
    {
        public static ResearchCandidateProjection From(ResearchPageCandidate page) =>
            new(
                page.Id,
                page.Title,
                page.WikiType,
                page.Confidence,
                page.Updated,
                page.Expires,
                page.Freshness,
                page.VaultRelativePath,
                page.IsOptedIn);
    }
}

public sealed record ResearchPageNavigation(
    ResearchCatalogSnapshot Snapshot,
    string? TopicId);

public sealed class ResearchRelatedTaskRow
{
    private readonly bool _canRepair;

    public ResearchRelatedTaskRow(
        ResearchRelatedTask task,
        bool canRepair = false)
    {
        Task = task;
        _canRepair = task.CanRepair || canRepair;
        Title = task.RelationState == ResearchTaskRelationState.MissingTask
            ? $"Missing Task: {task.TaskId}"
            : task.Title;
        var status = task.Status == "missing"
            ? "Missing"
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                task.Status.Replace('-', ' '));
        var relation = task.RelationState switch
        {
            ResearchTaskRelationState.MissingTaskReciprocalLink =>
                "Task is missing the Topic reference",
            ResearchTaskRelationState.MissingTopicReciprocalLink =>
                "Topic is missing the Task reference",
            ResearchTaskRelationState.MissingTask =>
                "Task no longer exists",
            _ => null,
        };
        StatusLabel = relation is null ? status : $"{status} · {relation}";
        AccessibleName = $"{Title}, {StatusLabel}";
        RepairAccessibleName = $"Repair Related Work link for {Title}";
    }

    public ResearchRelatedTask Task { get; }
    public string Title { get; }
    public string StatusLabel { get; }
    public string AccessibleName { get; }
    public string RepairAccessibleName { get; }
    public bool CanNavigate => Task.CanNavigate;
    public Visibility RepairVisibility => _canRepair
        ? Visibility.Visible
        : Visibility.Collapsed;
}

public sealed class ResearchRelatedWorkWarningRow
{
    public ResearchRelatedWorkWarningRow(ResearchRelatedWorkWarning warning)
    {
        Title = warning.Code switch
        {
            ResearchRelatedWorkWarningCode.InvalidMetadata =>
                "Malformed Related Work metadata",
            ResearchRelatedWorkWarningCode.InvalidTaskId =>
                "Invalid Related Work Task ID",
            ResearchRelatedWorkWarningCode.DuplicateTaskId =>
                $"Duplicate Task reference: {warning.Reference}",
            ResearchRelatedWorkWarningCode.MissingTask =>
                $"Missing Task: {warning.Reference}",
            ResearchRelatedWorkWarningCode.MissingTaskReciprocalLink =>
                $"Task reference needs repair: {warning.Reference}",
            _ => $"Topic reference needs repair: {warning.Reference}",
        };
        Message = warning.Message;
    }

    public string Title { get; }
    public string Message { get; }
}

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
            ResearchContextWarningCode.InvalidOverride =>
                $"Invalid context override: {warning.Reference}",
            ResearchContextWarningCode.DuplicateOverride =>
                $"Duplicate context override: {warning.Reference}",
            ResearchContextWarningCode.TopicLocked =>
                "The Research Topic is always included",
            _ => $"Ambiguous related page: {warning.Reference}",
        };
        Message = warning.Message;
    }

    public string Title { get; }
    public string Message { get; }
}

public sealed class ResearchContextSelectionRow : INotifyPropertyChanged
{
    private readonly ResearchDrawerMode _mode;
    private bool _isSelected;

    private ResearchContextSelectionRow(
        string id,
        string title,
        string metadata,
        bool isTopic,
        bool isSelected,
        ResearchDrawerMode mode)
    {
        Id = id;
        Title = title;
        Metadata = metadata;
        IsTopic = isTopic;
        _isSelected = isSelected;
        _mode = mode;
        IsEnabled = !isTopic;
        AccessibleName = isTopic
            ? $"{title}, Research Topic, always included"
            : mode == ResearchDrawerMode.SessionSelection
                ? $"Include {title} in the next Research Session"
                : $"Include {title} in durable Research context";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Title { get; }
    public string Metadata { get; }
    public bool IsTopic { get; }
    public bool IsEnabled { get; }
    public string AccessibleName { get; }
    public string StatusLabel => IsTopic
        ? "Always included"
        : _mode == ResearchDrawerMode.SessionSelection
            ? IsSelected ? "Selected" : "Not selected"
            : IsSelected ? "Included" : "Excluded";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(StatusLabel)));
        }
    }

    internal static ResearchContextSelectionRow Topic(
        ResearchTopic topic,
        ResearchDrawerMode mode) =>
        new(
            topic.Id,
            topic.Title,
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(topic.WikiType),
            isTopic: true,
            isSelected: true,
            mode);

    internal static ResearchContextSelectionRow Related(
        ResearchContextPage page,
        bool isSelected,
        ResearchDrawerMode mode) =>
        new(
            page.Id,
            page.Title,
            ResearchRelatedPageRow.BuildMetadataLine(page),
            isTopic: false,
            isSelected,
            mode);

    internal static ResearchContextSelectionRow Candidate(
        ResearchPageCandidate page,
        bool isSelected,
        ResearchDrawerMode mode) =>
        new(
            page.Id,
            page.Title,
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(page.WikiType),
            isTopic: false,
            isSelected,
            mode);
}

public enum ResearchDrawerMode
{
    None,
    Preview,
    SessionSelection,
    DurableCuration,
}
