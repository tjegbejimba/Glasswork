// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
    }

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
