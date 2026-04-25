// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Glasswork.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        AdoBaseUrlBox.Text = App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty;

        var theme = (App.UiState.Get<string>(App.ThemeKey) ?? "system").ToLowerInvariant();
        ThemeComboBox.SelectedIndex = theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };

        RefreshVaultInfo();
    }

    // ── Vault ────────────────────────────────────────────────────────────────

    private void RefreshVaultInfo()
    {
        var path = App.Vault?.VaultPath ?? string.Empty;
        VaultPathBox.Text = path;

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            VaultInfoText.Text = string.Empty;
            return;
        }

        try
        {
            var taskCount = Directory.GetFiles(path, "*.md", SearchOption.TopDirectoryOnly)
                .Count(f => !Path.GetFileName(f).StartsWith('_'));
            var lastWrite = Directory.GetLastWriteTime(path);
            VaultInfoText.Text = $"{taskCount} task file{(taskCount == 1 ? "" : "s")} · last modified {lastWrite:g}";
        }
        catch
        {
            VaultInfoText.Text = string.Empty;
        }
    }

    private async void SwitchVaultButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        // WinUI 3 requires the picker to be associated with the window handle.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var chosenPath = folder.Path;
        var validationResult = VaultValidator.Validate(chosenPath);

        VaultWarningBar.IsOpen = false;

        if (validationResult == VaultValidationResult.NotFound)
        {
            VaultWarningBar.Title = "Folder not found";
            VaultWarningBar.Message = $"'{chosenPath}' does not exist or could not be read.";
            VaultWarningBar.Severity = InfoBarSeverity.Error;
            VaultWarningBar.IsOpen = true;
            return;
        }

        if (validationResult == VaultValidationResult.HasMarkdownFiles)
        {
            VaultWarningBar.Title = "No .obsidian folder found";
            VaultWarningBar.Message =
                "This folder contains .md files but no .obsidian directory. " +
                "It may not be an Obsidian vault. The vault has been set anyway.";
            VaultWarningBar.Severity = InfoBarSeverity.Warning;
            VaultWarningBar.IsOpen = true;
        }
        else if (validationResult == VaultValidationResult.Empty)
        {
            VaultWarningBar.Title = "Folder looks empty";
            VaultWarningBar.Message =
                "This folder contains no .md files and no .obsidian directory. " +
                "Make sure this is the right location. The vault has been set anyway.";
            VaultWarningBar.Severity = InfoBarSeverity.Warning;
            VaultWarningBar.IsOpen = true;
        }

        App.SwitchVault(chosenPath);
        RefreshVaultInfo();

        // Update the status bar vault path text.
        if (App.MainWindow is MainWindow mw)
            mw.RefreshStatusBar();
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item) return;
        var value = item.Tag as string ?? "system";
        var existing = (App.UiState.Get<string>(App.ThemeKey) ?? "system").ToLowerInvariant();
        if (value == existing) return;

        if (value == "system")
            App.UiState.Remove(App.ThemeKey);
        else
            App.UiState.Set(App.ThemeKey, value);
        App.ScheduleUiStateSave();

        if (App.MainWindow is not null) App.ApplyTheme(App.MainWindow);
    }

    // ── Azure DevOps ─────────────────────────────────────────────────────────

    private void AdoBaseUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var trimmed = (AdoBaseUrlBox.Text ?? string.Empty).Trim();
        var existing = App.UiState.Get<string>(App.AdoBaseUrlKey) ?? string.Empty;
        if (trimmed == existing) return;
        if (string.IsNullOrEmpty(trimmed))
        {
            App.UiState.Remove(App.AdoBaseUrlKey);
        }
        else
        {
            App.UiState.Set(App.AdoBaseUrlKey, trimmed);
        }
        App.ScheduleUiStateSave();
    }
}

