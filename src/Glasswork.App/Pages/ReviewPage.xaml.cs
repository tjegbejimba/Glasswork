using System;
using System.Collections.Generic;
using System.Linq;
using Glasswork.Services;
using Glasswork.Core.Models;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Glasswork.Pages;

public sealed partial class ReviewPage : Page
{
    public ReviewPageViewModel ViewModel { get; }

    public ReviewPage()
    {
        ViewModel = new ReviewPageViewModel(App.Vault, App.ReviewQueue);
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
        RefreshChrome();
        RefreshShellReviewState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.ClearSelection();
        RefreshChrome();
    }

    private void ToggleSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string itemId })
            return;

        ViewModel.ToggleItemSelection(itemId);
        RefreshChrome();
    }

    private async void ApproveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Approval.RequiresConfirmation)
        {
            var lines = string.Join(Environment.NewLine, ViewModel.Approval.MutationSummaryLines);
            var dialog = new ContentDialog
            {
                Title = "Approve selected changes",
                Content = new TextBlock
                {
                    Text = lines,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    IsTextSelectionEnabled = true,
                },
                PrimaryButtonText = "Approve",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            dialog.WithAppTheme(this);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        ViewModel.ApproveSelected();
        RefreshChrome();
        RefreshShellReviewState();
    }

    private async void RejectSelected_Click(object sender, RoutedEventArgs e)
    {
        var reasonBox = new ComboBox
        {
            PlaceholderText = "Optional reason",
            ItemsSource = new List<string>
            {
                "Outdated evidence",
                "Incorrect task match",
                "Not applicable",
                "Not enough evidence",
            },
        };
        var freeTextBox = new TextBox
        {
            Header = "Optional details",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(reasonBox);
        content.Children.Add(freeTextBox);

        var dialog = new ContentDialog
        {
            Title = "Reject selected proposals",
            Content = content,
            PrimaryButtonText = "Reject",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dialog.WithAppTheme(this);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var reason = ComposeReason(reasonBox.SelectedItem as string, freeTextBox.Text);
        ViewModel.RejectSelected(reason);
        RefreshChrome();
        RefreshShellReviewState();
    }

    private void AcknowledgeRecovery_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AcknowledgeRecovery();
        RefreshChrome();
        RefreshShellReviewState();
    }

    private async void OpenSourceLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url })
            return;

        if (ArtifactLinkPolicy.Decide(url) != ArtifactLinkPolicy.Decision.Allow)
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        await Launcher.LaunchUriAsync(uri);
    }

    private void RefreshChrome()
    {
        ApprovalBlockedText.Visibility = ViewModel.Approval.BlockingMessages.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApprovalBlockedText.Text = ViewModel.Approval.BlockingMessages.FirstOrDefault() ?? string.Empty;

        RecoveryWarningBar.IsOpen = ViewModel.RecoveryWarning is not null;
        RecoveryWarningBar.Message = ViewModel.RecoveryWarning?.Message
            ?? "Source cursors stay paused until the recovery warning is acknowledged.";

        SourceHealthPanel.Visibility = ViewModel.SourceHealthEntries.Any(entry => entry.IsDegraded)
            ? Visibility.Visible
            : Visibility.Collapsed;

        WaitingForRefreshSection.Visibility = ViewModel.WaitingForRefreshGroups.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshShellReviewState()
    {
        (App.MainWindow as MainWindow)?.RefreshReviewNavigationState();
    }

    private static string? ComposeReason(string? selectedReason, string freeText)
    {
        var trimmedReason = string.IsNullOrWhiteSpace(selectedReason) ? null : selectedReason.Trim();
        var trimmedText = string.IsNullOrWhiteSpace(freeText) ? null : freeText.Trim();
        return (trimmedReason, trimmedText) switch
        {
            (null, null) => null,
            ({ } reason, null) => reason,
            (null, { } text) => text,
            ({ } reason, { } text) => $"{reason}: {text}",
        };
    }
}
