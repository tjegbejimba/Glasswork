using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Glasswork.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Glasswork;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "myday":
                    NavFrame.Navigate(typeof(MyDayPage));
                    break;
                case "backlog":
                    NavFrame.Navigate(typeof(BacklogPage));
                    break;
                case "worklog":
                    NavFrame.Navigate(typeof(WorkLogPage));
                    break;
                case "feedback":
                    ShowFeedbackDialog();
                    // Deselect so it acts like a button, not a nav destination
                    sender.SelectedItem = null;
                    return;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }

    private async void ShowFeedbackDialog()
    {
        var dialog = new FeedbackDialog
        {
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.CreatedUrl is not null)
        {
            // Show success tip
            var tip = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Feedback Submitted",
                Content = $"Issue created: {dialog.CreatedUrl}",
                CloseButtonText = "OK"
            };
            await tip.ShowAsync();
        }
    }
}
