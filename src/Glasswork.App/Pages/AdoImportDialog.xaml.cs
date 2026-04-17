using System;
using System.Collections.Generic;
using Glasswork.Core.Models;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Glasswork.Pages;

public sealed partial class AdoImportDialog : ContentDialog
{
    public AdoWorkItem? SelectedItem { get; private set; }

    private readonly AdoService? _ado;

    public AdoImportDialog()
    {
        InitializeComponent();

        // ADO service requires configuration — null if not configured
        _ado = App.Ado;
        if (_ado == null)
        {
            SearchBox.PlaceholderText = "ADO not configured. Set PAT in settings.";
            SearchBox.IsEnabled = false;
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await SearchAsync();
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        if (_ado == null) return;

        LoadingRing.IsActive = true;
        try
        {
            var results = await _ado.SearchAssignedAsync(SearchBox.Text);
            ResultsList.ItemsSource = results;
        }
        catch
        {
            ResultsList.ItemsSource = new List<AdoWorkItem>();
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedItem = ResultsList.SelectedItem as AdoWorkItem;
        IsPrimaryButtonEnabled = SelectedItem != null;
    }
}
