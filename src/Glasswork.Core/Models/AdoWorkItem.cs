using CommunityToolkit.Mvvm.ComponentModel;

namespace Glasswork.Core.Models;

/// <summary>
/// Represents an Azure DevOps work item pulled from the REST API.
/// </summary>
public partial class AdoWorkItem : ObservableObject
{
    [ObservableProperty] public partial int Id { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string State { get; set; } = "";
    [ObservableProperty] public partial string WorkItemType { get; set; } = "";
    [ObservableProperty] public partial string? AssignedTo { get; set; }
    [ObservableProperty] public partial string? AreaPath { get; set; }
    [ObservableProperty] public partial string? IterationPath { get; set; }
    [ObservableProperty] public partial string Url { get; set; } = "";
}
