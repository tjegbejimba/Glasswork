using System;
using Glasswork.Core.Models;
using Glasswork.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Glasswork.Pages;

public sealed partial class MyDayPage : Page
{
    public MyDayViewModel ViewModel { get; }
    public string TodayDate => DateTime.Today.ToString("dddd, MMMM d");

    public MyDayPage()
    {
        ViewModel = new MyDayViewModel(App.Vault, App.Tasks);
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Refresh();
    }

    private void CompleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.CompleteTaskCommand.Execute(task);
        }
    }

    private void RemoveFromDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.RemoveFromMyDayCommand.Execute(task);
        }
    }

    private void AddToDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GlassworkTask task })
        {
            ViewModel.AddToMyDayCommand.Execute(task);
        }
    }

    private void CarryAll_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CarryAllCommand.Execute(null);
    }
}
