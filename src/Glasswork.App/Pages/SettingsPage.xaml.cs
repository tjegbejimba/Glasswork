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
