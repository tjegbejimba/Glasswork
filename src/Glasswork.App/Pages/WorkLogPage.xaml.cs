using System;
using Glasswork.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Glasswork.Pages;

public sealed partial class WorkLogPage : Page
{
    private readonly WorkLogService _workLog;
    private DateTime _currentWeekStart;
    private string _currentLog = "";

    public WorkLogPage()
    {
        _workLog = new WorkLogService(App.Vault);
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _currentWeekStart = GetMondayOfWeek(DateTime.Today);
        RefreshLog();
    }

    private void RefreshLog()
    {
        _currentLog = _workLog.GenerateWeeklyLog(_currentWeekStart);
        LogContent.Text = _currentLog;
        WeekLabel.Text = $"Week of {_currentWeekStart:MMM d, yyyy}";
    }

    private void PrevWeek_Click(object sender, RoutedEventArgs e)
    {
        _currentWeekStart = _currentWeekStart.AddDays(-7);
        RefreshLog();
    }

    private void NextWeek_Click(object sender, RoutedEventArgs e)
    {
        _currentWeekStart = _currentWeekStart.AddDays(7);
        RefreshLog();
    }

    private void ThisWeek_Click(object sender, RoutedEventArgs e)
    {
        _currentWeekStart = GetMondayOfWeek(DateTime.Today);
        RefreshLog();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(_currentLog);
        Clipboard.SetContent(dp);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _workLog.GenerateAndSave(_currentWeekStart);
    }

    private static DateTime GetMondayOfWeek(DateTime date)
    {
        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }
}
