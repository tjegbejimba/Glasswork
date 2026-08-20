using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Glasswork.Pages;

public sealed partial class PlannerPage : Page
{
    private readonly PlannerViewModel _viewModel;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _sessionTimer;
    private bool _rendering;
    private bool _subscribed;
    private CancellationTokenSource _navigationCancellation = new();

    public PlannerPage()
    {
        InitializeComponent();
        _viewModel = new PlannerViewModel(
            App.Vault,
            App.Tasks,
            App.Index,
            App.UiState,
            App.Mutations,
            App.TaskQuery,
            calendarContext: App.CalendarContext);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.NotTodayTray.CollectionChanged += (_, _) => RenderRecovery();
        _sessionTimer = DispatcherQueue.CreateTimer();
        _sessionTimer.Interval = TimeSpan.FromMilliseconds(500);
        _sessionTimer.Tick += (_, _) =>
        {
            _viewModel.ProcessSessionTime();
            RenderRecovery();
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_navigationCancellation.IsCancellationRequested)
        {
            _navigationCancellation.Dispose();
            _navigationCancellation = new CancellationTokenSource();
        }
        if (!_subscribed)
        {
            App.Index.TasksChanged += Index_TasksChanged;
            _subscribed = true;
        }
        _sessionTimer.Start();
        await RefreshPlannerAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _sessionTimer.Stop();
        _navigationCancellation.Cancel();
        if (_subscribed)
        {
            App.Index.TasksChanged -= Index_TasksChanged;
            _subscribed = false;
        }
        _viewModel.EndSession();
        base.OnNavigatedFrom(e);
    }

    private void Index_TasksChanged(object? sender, TasksChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => _ = RefreshPlannerAsync());

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RenderState);

    private void RefreshPlanner()
    {
        _viewModel.Refresh();
        RenderState();
    }

    private async Task RefreshPlannerAsync(bool forceCalendarRefresh = false)
    {
        var refresh = _viewModel.RefreshAsync(
            forceCalendarRefresh,
            _navigationCancellation.Token);
        RenderState();
        await refresh;
        RenderState();
    }

    private void RenderState()
    {
        _rendering = true;
        try
        {
            GroupList.ItemsSource = _viewModel.Groups;
            SetupPanel.Visibility = _viewModel.ProfileStatus == PlannerProfileLoadStatus.SetupRequired
                ? Visibility.Visible
                : Visibility.Collapsed;
            ProfileStateText.Text = _viewModel.ProfileStatus switch
            {
                PlannerProfileLoadStatus.Ready => "Planner Profile confirmed",
                PlannerProfileLoadStatus.SetupRequired => "Setup required",
                PlannerProfileLoadStatus.UnsupportedVersion => "Planner Profile requires a newer Glasswork version",
                _ => "Planner Profile is invalid and preserved",
            };
            TotalsText.Text = $"{_viewModel.SelectedWorkMinutes} min selected";
            UncertaintyText.Text =
                $"{_viewModel.AssumedSizeCount} assumed, {_viewModel.UncertainSizeCount} check";
            CalendarStateText.Text = _viewModel.CalendarStatus;
            CalendarSetupPanel.Visibility = _viewModel.CanConnectCalendar
                ? Visibility.Visible
                : Visibility.Collapsed;
            CalendarRefreshButton.Visibility = _viewModel.CanRefreshCalendar
                ? Visibility.Visible
                : Visibility.Collapsed;
            CalendarDisconnectButton.Visibility = _viewModel.CanDisconnectCalendar
                ? Visibility.Visible
                : Visibility.Collapsed;
            CalendarRecoveryPanel.Visibility = _viewModel.CanResetCalendar
                ? Visibility.Visible
                : Visibility.Collapsed;
            CalendarResetScopeText.Text = _viewModel.CalendarResetScopeText;
            ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(_viewModel.ErrorMessage);
            ErrorBar.Message = _viewModel.ErrorMessage ?? string.Empty;
            AnnouncementText.Text = _viewModel.Announcement;

            if (_viewModel.ProfileStatus == PlannerProfileLoadStatus.SetupRequired)
                PopulateProfileDraft(_viewModel.ProfileDraft);
            RenderRecovery();
        }
        finally
        {
            _rendering = false;
        }

        if (_viewModel.FocusTargetIdentity is not null)
            DispatcherQueue.TryEnqueue(FocusPlannerTarget);
    }

    private void PopulateProfileDraft(PlannerProfileDraft draft)
    {
        CapacityBox.Text = draft.DailyCapacityMinutes.ToString(CultureInfo.InvariantCulture);
        WorkStartBox.Text = draft.WorkStartLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        WorkEndBox.Text = draft.WorkEndLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        LunchStartBox.Text = draft.LunchStartLocal?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        LunchEndBox.Text = draft.LunchEndLocal?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        BufferBox.Text = draft.TransitionBufferMinutes.ToString(CultureInfo.InvariantCulture);
    }

    private void RenderRecovery()
    {
        if (UndoPanel is null)
            return;
        UndoPanel.Visibility = _viewModel.InlineUndo is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        UndoText.Text = _viewModel.InlineUndo is null
            ? string.Empty
            : $"{_viewModel.InlineUndo.Title} moved out of My Day.";
        TrayList.ItemsSource = _viewModel.NotTodayTray;
        TrayExpander.Visibility = _viewModel.NotTodayTray.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ConfirmProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CapacityBox.Text, out var capacity)
            || !TimeOnly.TryParse(WorkStartBox.Text, out var workStart)
            || !TimeOnly.TryParse(WorkEndBox.Text, out var workEnd)
            || !ParseOptionalTime(LunchStartBox.Text, out var lunchStart)
            || !ParseOptionalTime(LunchEndBox.Text, out var lunchEnd)
            || !int.TryParse(BufferBox.Text, out var buffer))
        {
            ErrorBar.Message = "Use valid whole minutes and local times such as 09:00.";
            ErrorBar.IsOpen = true;
            return;
        }

        _viewModel.ConfirmProfile(new PlannerProfileDraft
        {
            DailyCapacityMinutes = capacity,
            WorkStartLocal = workStart,
            WorkEndLocal = workEnd,
            LunchStartLocal = lunchStart,
            LunchEndLocal = lunchEnd,
            TransitionBufferMinutes = buffer,
            SelectedCalendarReferences = _viewModel.ProfileDraft.SelectedCalendarReferences,
        });
        RenderState();
    }

    private static bool ParseOptionalTime(string value, out TimeOnly? parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }
        if (TimeOnly.TryParse(value, out var time))
        {
            parsed = time;
            return true;
        }
        parsed = null;
        return false;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshPlannerAsync(forceCalendarRefresh: true);

    private async void CalendarConnect_Click(object sender, RoutedEventArgs e)
    {
        var secret = CalendarSecretBox.Password;
        CalendarSecretBox.Password = string.Empty;
        await _viewModel.ConnectCalendarAsync(secret, _navigationCancellation.Token);
        RenderState();
    }

    private async void CalendarRefresh_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.RefreshCalendarAsync(_navigationCancellation.Token);
        RenderState();
    }

    private async void CalendarDisconnect_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.DisconnectCalendarAsync(_navigationCancellation.Token);
        RenderState();
    }

    private async void CalendarReset_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResetCalendarAsync(_navigationCancellation.Token);
        RenderState();
    }

    private void SizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rendering || sender is not Button { Tag: PlannerActionableLeaf leaf } button)
            return;

        var flyout = new MenuFlyout();
        AddSizeChoice(flyout, leaf, "Clear size", null);
        AddSizeChoice(flyout, leaf, "Set Quick size", "quick");
        AddSizeChoice(flyout, leaf, "Set Short size", "short");
        AddSizeChoice(flyout, leaf, "Set Focus size", "focus");
        AddSizeChoice(flyout, leaf, "Set Deep size", "deep");
        AddSizeChoice(flyout, leaf, "Set Break down size", "break_down");
        flyout.ShowAt(button);
    }

    private void AddSizeChoice(
        MenuFlyout flyout,
        PlannerActionableLeaf leaf,
        string label,
        string? size)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += (_, _) =>
        {
            _viewModel.SetSize(leaf, size);
            RenderState();
        };
        flyout.Items.Add(item);
    }

    private void LeafNotToday_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlannerActionableLeaf leaf })
        {
            _viewModel.NotToday(leaf);
            RenderState();
        }
    }

    private void GroupNotToday_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlannerScopeGroup group })
        {
            _viewModel.NotToday(group);
            RenderState();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.UndoNotToday();
        RenderState();
    }

    private void Undo_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = _viewModel.UndoNotToday();
        RenderState();
    }

    private void PlannerPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.U || !e.KeyStatus.IsMenuKeyDown)
            return;

        e.Handled = _viewModel.UndoNotToday();
        RenderState();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlannerNotTodayRecovery recovery })
        {
            _viewModel.RestoreNotToday(recovery);
            RenderState();
        }
    }

    private async void FocusPlannerTarget()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(50);
            if (FindTargetButton(GroupList, _viewModel.FocusTargetIdentity!) is Button button)
            {
                button.Focus(FocusState.Programmatic);
                return;
            }
        }
    }

    private static Button? FindTargetButton(DependencyObject root, string identity)
    {
        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button button
                && button.Tag is PlannerActionableLeaf leaf
                && button.Content is string content
                && content.StartsWith("Not today", StringComparison.Ordinal)
                && string.Equals(leaf.Identity, identity, StringComparison.Ordinal))
            {
                return button;
            }
            var nested = FindTargetButton(child, identity);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}
